using Workman.Core.Queries;
using Workman.Core.Writing;
using Workman.Mcp;

namespace Workman.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>
    /// work_agenda as JSON (deadlines/6): the same <see cref="BoardQueries.AgendaAsync"/> record, one read per scope,
    /// as of the injected clock. With <c>scope</c>, the subtree under that item; without, every unarchived project
    /// the caller can see (rule 8), the lists merged in the query's order.
    /// </summary>
    private static async Task<IResult> AgendaAsync(
        string? until,
        string? since,
        Guid? scope,
        BoardQueries board,
        ApiQueries api,
        TimeProvider clock,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var (untilValue, untilProblem) = string.IsNullOrWhiteSpace(until) ? (null, "until is missing") : DateValue.Parse(until);
        var (sinceValue, sinceProblem) = string.IsNullOrWhiteSpace(since) ? (null, null) : DateValue.Parse(since);
        if (untilValue is not { } window || sinceProblem is not null)
        {
            var (name, problem) = untilProblem is not null ? ("until", untilProblem) : ("since", sinceProblem!);
            return Results.BadRequest(new
            {
                error = $"{name}: {problem}. Give a day YYYY-MM-DD (that whole day in the workspace's zone) "
                        + "or an ISO date-time with an offset, e.g. 2026-11-20T17:00:00+01:00",
            });
        }

        var book = new V1Book(api, board);
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);

        List<Guid> scopes;
        if (scope is { } id)
        {
            if (!await KnownAsync(book, snapshot, id, cancellationToken))
            {
                return NoItem(id);
            }

            scopes = [id];
        }
        else
        {
            scopes = [.. (await board.ProjectsAsync(snapshot.WorkspaceId, cancellationToken))
                .Where(p => p.ArchivedAt is null)
                .Select(p => p.Id)];
        }

        var now = clock.GetUtcNow();
        var agendas = new List<Agenda>();
        foreach (var root in scopes)
        {
            agendas.Add(await board.AgendaAsync(snapshot.WorkspaceId, root, root, window, sinceValue, now, cancellationToken));
        }

        var bounds = agendas.Count > 0
            ? agendas[0]
            : await board.AgendaAsync(snapshot.WorkspaceId, Guid.Empty, null, window, sinceValue, now, cancellationToken);

        var overdue = agendas.SelectMany(a => a.Overdue).OrderBy(r => r.DueEnd).ThenBy(r => r.ReadySince).ToList();
        var due = agendas.SelectMany(a => a.DueBy).OrderBy(r => r.DueEnd).ThenBy(r => r.ReadySince).ToList();
        var actionable = agendas.SelectMany(a => a.Actionable).OrderBy(r => r.ReadySince).ToList();

        var rows = overdue.Concat(due).Concat(actionable).ToList();
        await book.ItemsAsync(rows.Select(r => r.Id).Concat(rows.SelectMany(r => r.BlockerIds ?? [])), cancellationToken);

        AgendaRowView View(AgendaRow row)
            => new(
                book.Item(row.Id),
                book.Item(row.Id).Status,
                row.Due,
                row.NotBefore,
                row.DueEnd,
                row.ReadySince,
                row.HeldBy,
                [.. (row.BlockerIds ?? []).Where(book.Has).Select(book.Item)]);

        return Results.Ok(new AgendaView(
            now,
            snapshot.Seq,
            bounds.Zone,
            bounds.UntilEnd,
            bounds.SinceStart,
            scope is { } scoped ? book.Item(scoped) : null,
            [.. overdue.Select(View)],
            [.. due.Select(View)],
            [.. actionable.Select(View)]));
    }
}
