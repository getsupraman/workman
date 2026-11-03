using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Mcp;

namespace Workman.Api.App.V1;

public static partial class ApiV1
{
    private static readonly HashSet<string> ContainerKinds = ["project", "feature", "run", "phase"];

    private const int BoardsMaxRuns = 200;

    /// <summary>
    /// The subtree under any item at or below a run, as one flat pre-order list, the item itself first
    /// at depth 0. By id, so a run <c>backlog</c> and a phase <c>m0/backlog</c> are two boards, never a
    /// 404 over which one a ref meant.
    /// </summary>
    private static async Task<IResult> BoardAsync(
        Guid id,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var book = new V1Book(api, board);
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);
        if (!await KnownAsync(book, snapshot, id, cancellationToken))
        {
            return NoItem(id);
        }

        if (book.RunId(id) is null)
        {
            return Results.NotFound(new { error = $"item {id} is not a run and sits under none" });
        }

        return Results.Ok((await BoardViewsAsync([id], book, snapshot, board, api, cancellationToken))[0]);
    }

    /// <summary>
    /// Many runs' boards from one snapshot, each what <see cref="BoardAsync"/> returns for that run. Without
    /// <c>runs</c>, every run of every unarchived project the caller sees, in the overview's order; with it,
    /// those runs, leaving out an id that is no run the caller sees.
    /// </summary>
    private static async Task<IResult> BoardsAsync(
        string? runs,
        BoardQueries board,
        ApiQueries api,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var asked = new List<Guid>();
        foreach (var part in (runs ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var parsed))
            {
                return Results.BadRequest(new { error = $"runs: '{part}' is not an id" });
            }

            asked.Add(parsed);
        }

        asked = [.. asked.Distinct()];
        if (asked.Count > BoardsMaxRuns)
        {
            return Results.BadRequest(new { error = $"runs: at most {BoardsMaxRuns} ids, got {asked.Count}" });
        }

        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);
        var book = new V1Book(api, board);

        if (runs is null)
        {
            var projects = (await board.ProjectsAsync(snapshot.WorkspaceId, cancellationToken))
                .Where(p => p.ArchivedAt is null)
                .Select(p => p.Id)
                .ToList();
            var order = projects.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index);
            asked = [.. (await api.ProjectRunsAsync(projects, cancellationToken))
                .OrderBy(r => order[r.ProjectId])
                .Select(r => r.RunId)
                .Distinct()];
        }

        await book.PlacedAsync(asked, cancellationToken);
        var known = asked
            .Where(id => book.Has(id) && book.Row(id).WorkspaceId == snapshot.WorkspaceId && book.RunId(id) == id)
            .ToList();

        return Results.Ok(new BoardsView(
            snapshot.AsOf,
            snapshot.Seq,
            await BoardViewsAsync(known, book, snapshot, board, api, cancellationToken)));
    }

    /// <summary>
    /// The boards under <paramref name="roots"/>, each placed in <paramref name="book"/> already. One query per
    /// root reads its rows; everything else is read once for all of them.
    /// </summary>
    private static async Task<IReadOnlyList<BoardView>> BoardViewsAsync(
        IReadOnlyList<Guid> roots,
        V1Book book,
        Snapshot snapshot,
        BoardQueries board,
        ApiQueries api,
        CancellationToken cancellationToken)
    {
        var trees = new List<(Guid Root, Guid RunId, IReadOnlyList<BoardRow> Rows)>();
        foreach (var root in roots)
        {
            trees.Add((root, book.RunId(root)!.Value, await api.BoardAsync(root, cancellationToken)));
        }

        var ids = trees.SelectMany(t => t.Rows).Select(r => r.Id).Distinct().ToList();
        await book.ItemsAsync(ids, cancellationToken);

        var rollups = (await api.RollupsAsync(ids, cancellationToken)).ToDictionary(r => r.Id);
        var claims = (await book.ClaimsAsync(await api.HeldClaimsAsync(roots, cancellationToken), snapshot.AsOf, cancellationToken))
            .ToDictionary(c => c.Item.Id);
        var links = await board.LinksAsync(ids, cancellationToken);
        await book.ItemsAsync(links.Select(l => l.FromId), cancellationToken);

        var blockedBy = links
            .Where(l => l.Type == LinkType.Blocks && !Closed(l.FromStatus))
            .ToLookup(l => l.ToId, l => l.FromId);

        var configs = (await api.RunConfigsAsync([.. trees.Select(t => t.RunId).Distinct()], cancellationToken))
            .ToDictionary(c => c.RunId);

        return [.. trees.Select(tree =>
        {
            var (id, runId, rows) = tree;
            var rootDepth = rows.Count == 0 ? 0 : rows[0].Depth;
            configs.TryGetValue(runId, out var config);

            var views = rows.Select(row =>
            {
                claims.TryGetValue(row.Id, out var claim);

                return new BoardRowView(
                    book.Item(row.Id),
                    row.ParentId,
                    row.Depth - rootDepth,
                    V1Text.Seq(row.Seq),
                    row.HasChildren,
                    V1Text.Resolution(row.Resolution),
                    row.Priority,
                    row.Severity,
                    new CriteriaCountView(row.CriteriaChecked, row.CriteriaTotal),
                    ContainerKinds.Contains(row.Kind) || row.HasChildren ? Rollup(rollups.GetValueOrDefault(row.Id)) : null,
                    claim,
                    row.Commit,
                    row is { VerificationPassed: { } passed, VerificationRunBy: { } runBy, VerificationAt: { } at }
                        ? new VerificationSummaryView(passed, V1Text.Snake(runBy), at)
                        : null,
                    [.. blockedBy[row.Id].Distinct().Select(book.Item)],
                    Flags(row, claim),
                    row.OpenedAt,
                    row.StartedAt,
                    row.ClosedAt);
            }).ToList();

            var root = book.Row(id);

            return new BoardView(
                snapshot.AsOf,
                snapshot.Seq,
                book.Item(runId),
                book.Item(book.ProjectId(id)),
                root.Intent ?? (runId == id ? null : book.Row(runId).Intent),
                config?.Branch,
                config?.Worktree,
                Rollup(rollups.GetValueOrDefault(id)),
                views);
        })];
    }

    /// <summary>
    /// The board's exceptions, in the vocabulary of doc 06 §2 read the way the text surfaces read it.
    /// <c>disagrees</c> is a closed container over unclosed leaves; <c>done_unverified</c> is the honesty count's.
    /// </summary>
    private static IReadOnlyList<string> Flags(BoardRow row, ClaimView? claim)
    {
        var flags = new List<string>();

        if (row.RollupTotal > 0 && row.RollupDone < row.RollupTotal && Closed(row.Status))
        {
            flags.Add("disagrees");
        }

        // BoardQueries.DoneUnverifiedAsync's predicate exactly — a NULL resolution fails its
        // `<> 'obsolete'` — so a board's flags always sum to the overview's count.
        if (row is { Status: ItemStatus.Done, RequiresVerify: true, OrchestratorVerified: false }
            && row.Resolution is { } resolution && resolution != ItemResolution.Obsolete)
        {
            flags.Add("done_unverified");
        }

        if (row.Forced)
        {
            flags.Add("forced");
        }

        if (claim is { Expired: true })
        {
            flags.Add("stale_claim");
        }

        if (row.UnconsumedNotes)
        {
            flags.Add("unconsumed_notes");
        }

        return flags;
    }

    private static bool Closed(ItemStatus status)
        => status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;
}
