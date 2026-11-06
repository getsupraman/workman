using Microsoft.AspNetCore.Mvc;
using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Core.Rendering;
using Workman.Mcp;

namespace Workman.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>The attention block's stale threshold (<c>BoardReader.StaleHours</c>).</summary>
    private const int StaleHours = 4;

    /// <summary>The longest <c>detail</c>, ellipsis included.</summary>
    private const int DetailLength = 200;

    private static async Task<IResult> InboxAsync(
        [FromQuery] Guid? project,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        WebBase web,
        CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);

        var book = new V1Book(api, board);
        IReadOnlyList<Guid> scopes;

        if (project is { } id)
        {
            if (!await KnownAsync(book, snapshot, id, cancellationToken))
            {
                return NoItem(id);
            }

            scopes = [id];
        }
        else
        {
            scopes = [.. (await board.ProjectsAsync(snapshot.WorkspaceId, cancellationToken)).Select(p => p.Id)];
        }

        var rows = await InboxRowsAsync(snapshot, scopes, book, board, api, web.Current, cancellationToken);
        return Results.Ok(new InboxView(snapshot.AsOf, snapshot.Seq, rows));
    }

    private sealed record Waiting(
        string Kind,
        Guid ItemId,
        string What,
        DateTimeOffset Since,
        Func<string, string> Call,
        string? Detail = null,
        string? Actor = null,
        string? Tree = null,
        int Count = 1);

    /// <summary>
    /// Everything waiting on a human under the scopes, newest first. Each source is the query the
    /// attention block uses for the same line (<c>BoardReader.AttentionAsync</c>), so the inbox and
    /// what an agent is told never disagree. One row per kind and item, so the key is stable.
    /// </summary>
    private static async Task<IReadOnlyList<InboxRowView>> InboxRowsAsync(
        Snapshot snapshot,
        IReadOnlyList<Guid> scopes,
        V1Book book,
        BoardQueries board,
        ApiQueries api,
        string? webBase,
        CancellationToken cancellationToken)
    {
        var waiting = new List<Waiting>();

        foreach (var scope in scopes)
        {
            foreach (var row in await api.BlockedOnAPersonAsync(scope, cancellationToken))
            {
                waiting.Add(new Waiting(
                    "blocked", row.Id, "blocked, and nothing open blocks it — it waits on a decision",
                    row.Since, q => $"work_status(ref:\"{q}\", to:\"in_progress\")"));
            }

            foreach (var blocker in await board.BlockerFindingsAsync(scope, cancellationToken))
            {
                var against = blocker.TargetRef is { Length: > 0 } target ? $" against {target}" : string.Empty;
                waiting.Add(new Waiting(
                    "blocker_finding", blocker.FindingId, $"BLOCKER finding open{against}: {blocker.FindingTitle}",
                    DateTimeOffset.MinValue, q => $"work_status(ref:\"{q}\", to:\"done\")"));
            }

            foreach (var question in await api.OpenQuestionsAsync(scope, cancellationToken))
            {
                waiting.Add(new Waiting(
                    "question", question.Id, "open question",
                    question.Since, q => $"work_status(ref:\"{q}\", to:\"done\")"));
            }

            foreach (var claim in await board.ExpiredClaimsAsync(scope, cancellationToken))
            {
                var (on, tree) = claim.Tree is { } t ? ($" on tree \"{t}\"", $", tree:\"{t}\"") : (string.Empty, string.Empty);
                waiting.Add(new Waiting(
                    "expired_claim", claim.ItemId, $"claim by {claim.AgentName}{on} expired",
                    claim.ExpiresAt, q => $"work_status(ref:\"{q}\", to:\"in_progress\"{tree})",
                    Actor: claim.AgentName, Tree: claim.Tree));
            }

            var due = (await board.AssumptionsAsync(snapshot.WorkspaceId, scope, true, cancellationToken))
                .Where(a => a.ScopeItemId is not null
                            && a.State == AssumptionState.Open
                            && a.RevisitStatus is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded)
                .GroupBy(a => a.ScopeItemId!.Value);

            foreach (var group in due)
            {
                var first = group.First();
                var count = group.Count();
                waiting.Add(new Waiting(
                    "assumption_due", group.Key,
                    $"{count} assumption(s) past revisit_when: {string.Join(", ", group.Select(a => a.Ref))}",
                    group.Max(a => a.RevisitClosedAt) ?? DateTimeOffset.MinValue,
                    _ => $"work_assume(ref:\"{first.Callable}\", resolve:\"confirmed\", evidence:\"…\")",
                    Detail: OneLine(first.Statement, count > 1 ? $" (+{count - 1} more)" : string.Empty),
                    Count: count));
            }

            foreach (var unverified in await board.DoneUnverifiedAsync(scope, cancellationToken))
            {
                waiting.Add(new Waiting(
                    "done_unverified", unverified.Id, "done with no orchestrator verification",
                    unverified.ClosedAt ?? DateTimeOffset.MinValue,
                    q => $"work_verify(ref:\"{q}\", result_line:\"…\", run_by:\"orchestrator\")"));
            }

            foreach (var stale in await board.StaleInProgressAsync(scope, StaleHours, cancellationToken))
            {
                waiting.Add(new Waiting(
                    "stale_in_progress", stale.Id, "in_progress with no live claim",
                    stale.StartedAt, q => $"work_status(ref:\"{q}\", to:\"in_progress\")"));
            }

            var notes = await board.UnconsumedNotesAsync(
                snapshot.WorkspaceId, scope, cancellationToken, openAddresseesOnly: true);

            foreach (var addressed in notes.Where(n => n.ToItemId is not null).GroupBy(n => n.ToItemId!.Value))
            {
                waiting.Add(new Waiting(
                    "unconsumed_note", addressed.Key, $"{addressed.Count()} note(s) addressed to it unconsumed",
                    addressed.Min(n => n.CreatedAt), q => $"work_brief(scope:\"{q}\", for:\"worker\")",
                    Detail: OneLine(addressed.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id).First().Body),
                    Count: addressed.Count()));
            }
        }

        await book.PlacedAsync(waiting.Select(w => w.ItemId), cancellationToken);

        // The facts the agents' lines do not carry: one query per source, for every row at once.
        var reasons = (await api.BlockedReasonsAsync(
                [.. waiting.Where(w => w.Kind == "blocked").Select(w => w.ItemId).Distinct()], cancellationToken))
            .ToDictionary(r => r.ItemId, r => r.Reason);
        var facts = (await api.InboxFactsAsync(
                [.. waiting.Where(w => w.Kind is "blocker_finding" or "done_unverified").Select(w => w.ItemId).Distinct()],
                cancellationToken))
            .ToDictionary(f => f.Id);
        await book.ItemsAsync(facts.Values.Select(f => f.ParentId), cancellationToken);

        return [.. waiting
            .DistinctBy(w => (w.Kind, w.ItemId))
            .Select(w =>
            {
                var item = book.Item(w.ItemId);
                var since = w.Since == DateTimeOffset.MinValue ? book.Row(w.ItemId).OpenedAt : w.Since;
                var fact = facts.GetValueOrDefault(w.ItemId);
                var target = w.Kind == "blocker_finding" ? book.Item(fact?.ParentId) : null;

                var detail = w.Kind switch
                {
                    "question" => OneLine(book.Row(w.ItemId).Intent),
                    "blocked" => OneLine(reasons.GetValueOrDefault(w.ItemId)),
                    "blocker_finding" => OneLine(Against(fact?.Source, target?.Title)),
                    _ => w.Detail,
                };

                return new InboxRowView(
                    $"{w.Kind}:{w.ItemId}",
                    w.Kind,
                    item,
                    book.RunOf(w.ItemId),
                    w.What,
                    since,
                    w.Call(item.QualifiedRef),
                    w.Kind is "question" or "blocked" ? "human" : "controller",
                    detail,
                    w.Kind == "done_unverified" ? fact?.ClosedBy : w.Actor,
                    w.Tree,
                    target,
                    w.Count,
                    webBase is not null && item.FullRef is { Length: > 0 } fullRef ? WebLinks.Link(webBase, fullRef) : null);
            })
            .OrderByDescending(r => r.Since)
            .ThenBy(r => r.Key, StringComparer.Ordinal)];
    }

    private static string? Against(string? source, string? targetTitle)
        => (source is { Length: > 0 }, targetTitle is { Length: > 0 }) switch
        {
            (true, true) => $"{source} · against: {targetTitle}",
            (true, false) => source,
            (false, true) => $"against: {targetTitle}",
            _ => null,
        };

    /// <summary>The first non-empty line of <paramref name="text"/>, shortened so that with <paramref name="suffix"/> it fits <see cref="DetailLength"/>.</summary>
    private static string? OneLine(string? text, string suffix = "")
    {
        var line = text?.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        return line is null ? null : Glyph.Row(line, Math.Max(2, DetailLength - suffix.Length)) + suffix;
    }
}
