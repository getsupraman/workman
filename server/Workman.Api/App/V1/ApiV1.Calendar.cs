using Workman.Core.Queries;
using Workman.Core.Writing;
using Workman.Mcp;

namespace Workman.Api.App.V1;

public static partial class ApiV1
{
    /// <summary>The widest range <c>/calendar</c> reads, from the start of <c>from</c> to the end of <c>to</c>.</summary>
    public const int CalendarMaxDays = 100;

    /// <summary>
    /// account-calendar/3: every dated item in a range, open and done, as of the injected clock. With <c>scope</c>, the
    /// subtree under that item; without, every unarchived project the caller can see (rule 8).
    /// </summary>
    private static async Task<IResult> CalendarAsync(
        string? from,
        string? to,
        Guid? scope,
        BoardQueries board,
        ApiQueries api,
        TimeProvider clock,
        RequestTenant tenant,
        CancellationToken cancellationToken)
    {
        var (fromValue, fromProblem) = string.IsNullOrWhiteSpace(from) ? (null, "from is missing") : DateValue.Parse(from);
        var (toValue, toProblem) = string.IsNullOrWhiteSpace(to) ? (null, "to is missing") : DateValue.Parse(to);
        if (fromValue is not { } start || toValue is not { } end)
        {
            var (name, problem) = fromProblem is not null ? ("from", fromProblem) : ("to", toProblem!);
            return Results.BadRequest(new
            {
                error = $"{name}: {problem}. Give a day YYYY-MM-DD (that whole day in the workspace's zone) "
                        + "or an ISO date-time with an offset, e.g. 2026-11-20T17:00:00+01:00",
            });
        }

        var book = new V1Book(api, board);
        var snapshot = await SnapshotAsync(api, tenant, cancellationToken);
        var bounds = await api.CalendarBoundsAsync(snapshot.WorkspaceId, start, end, cancellationToken);
        if (bounds.ToStart < bounds.FromStart)
        {
            return Results.BadRequest(new { error = $"to ({to}) is before from ({from}): give a to on or after from" });
        }

        if (bounds.ToEnd - bounds.FromStart > TimeSpan.FromDays(CalendarMaxDays))
        {
            return Results.BadRequest(new
            {
                error = $"the range from {from} to {to} is over {CalendarMaxDays} days: ask for at most {CalendarMaxDays} days at a time",
            });
        }

        if (scope is { } id && !await KnownAsync(book, snapshot, id, cancellationToken))
        {
            return NoItem(id);
        }

        var now = clock.GetUtcNow();
        var rows = await api.CalendarRangeAsync(snapshot.WorkspaceId, scope, bounds, end.At is not null, now, cancellationToken);
        var blockers = rows.ToDictionary(r => r.Id, r => r.BlockerIds.Select(Guid.Parse).ToList());
        await book.ItemsAsync(
            rows.Select(r => r.Id).Concat(blockers.Values.SelectMany(b => b)).Concat(scope is { } s ? [s] : []),
            cancellationToken);

        return Results.Ok(new CalendarView(
            now,
            snapshot.Seq,
            bounds.Zone,
            bounds.FromStart,
            bounds.ToEnd,
            scope is { } scoped ? book.Item(scoped) : null,
            [.. rows.Where(r => book.Has(r.Id)).Select(r => new CalendarRowView(
                book.Item(r.Id),
                r.Kind,
                r.Status,
                r.Due,
                r.NotBefore,
                r.DueEnd,
                r.NotBeforeStart,
                r.Late,
                r.ClosedAt,
                r.HeldBy,
                [.. blockers[r.Id].Where(book.Has).Select(book.Item)]))]));
    }
}
