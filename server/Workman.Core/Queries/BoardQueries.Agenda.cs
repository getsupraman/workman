using Workman.Core.Writing;

namespace Workman.Core.Queries;

/// <summary>
/// One open item on the agenda. <see cref="Ref"/> is bare inside the scope's run and qualified outside it;
/// <see cref="Due"/> is the written form (a day, or an instant in UTC); <see cref="Blockers"/> are the refs of
/// its unfinished <c>blocks</c> blockers.
/// </summary>
public sealed record AgendaRow(
    Guid Id,
    string Ref,
    string Title,
    string Status,
    string? Due,
    DateTimeOffset? DueEnd,
    string? HeldBy,
    string[] Blockers,
    DateTimeOffset? ReadySince);

/// <summary>
/// work_agenda's three lists, as of <see cref="Now"/>: overdue, due by <see cref="UntilEnd"/>, and ready now
/// after becoming so since <see cref="SinceStart"/>. The bounds are the instants the workspace's zone reads
/// <c>until</c> and <c>since</c> as. GET /api/v1/agenda renders the same record.
/// </summary>
public sealed record Agenda(
    string Zone,
    DateTimeOffset Now,
    DateTimeOffset UntilEnd,
    DateTimeOffset SinceStart,
    IReadOnlyList<AgendaRow> Overdue,
    IReadOnlyList<AgendaRow> DueBy,
    IReadOnlyList<AgendaRow> Actionable);

public sealed partial class BoardQueries
{
    private sealed record AgendaBounds(string Zone, DateTimeOffset UntilEnd, DateTimeOffset SinceStart);

    private sealed record AgendaHit(
        Guid Id,
        string Ref,
        string Title,
        string Status,
        string? Due,
        DateTimeOffset? DueEnd,
        string? HeldBy,
        string[] Blockers,
        DateTimeOffset? ReadySince,
        bool IsOverdue,
        bool IsDueBy,
        bool IsActionable);

    /// <summary>
    /// The agenda under <paramref name="scopeItemId"/>. A day <paramref name="until"/> runs to the end of that day,
    /// a day <paramref name="since"/> from its start, both in the workspace's zone; no <paramref name="since"/> means
    /// the start of today there. Ready means what work_next means — open or ready, no unfinished blocker, no open
    /// child, not_before reached — and it became ready at the latest of its not_before, its last blocker's close
    /// and its opening. Every date is read by the <c>ItemDates</c> SQL functions.
    /// </summary>
    public async Task<Agenda> AgendaAsync(
        Guid workspaceId,
        Guid scopeItemId,
        Guid? homeScopeId,
        DateValue until,
        DateValue? since,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string boundsSql =
            """
            SELECT z.zone AS "Zone",
                   date_value_end(@untilDay::date, @untilAt, z.zone) AS "UntilEnd",
                   coalesce(date_value_start(@sinceDay::date, @sinceAt, z.zone),
                            date_value_start((@now AT TIME ZONE z.zone)::date, NULL, z.zone)) AS "SinceStart"
            FROM   (SELECT timezone_of(@workspace) AS zone) z;
            """;

        var bounds = (await ListAsync<AgendaBounds>(boundsSql, new
        {
            workspace = workspaceId,
            untilDay = until.Day?.ToDateTime(TimeOnly.MinValue),
            untilAt = until.At,
            sinceDay = since?.Day?.ToDateTime(TimeOnly.MinValue),
            sinceAt = since?.At,
            now,
        }, cancellationToken))[0];

        const string sql =
            """
            WITH scope AS (
              SELECT id FROM item WHERE id = @scope OR ancestors @> ARRAY[@scope]::uuid[]
            ),
            open_item AS (
              SELECT i.*, item_due_end(i) AS due_end, item_not_before_start(i) AS nb_start
              FROM   item i JOIN scope s ON s.id = i.id
              WHERE  i.status NOT IN ('done','cancelled','superseded')
            ),
            judged AS (
              SELECT o.*,
                     ARRAY(SELECT CASE WHEN b.ref_scope_id IS NOT DISTINCT FROM @home OR bs.id IS NULL
                                       THEN b.ref ELSE bs.ref || '/' || b.ref END
                           FROM   item_link l
                           JOIN   item b ON b.id = l.from_item_id
                           LEFT   JOIN item bs ON bs.id = b.ref_scope_id AND bs.id <> b.id
                           WHERE  l.to_item_id = o.id AND l.type = 'blocks'
                             AND  b.status NOT IN ('done','cancelled','superseded')
                           ORDER  BY b.seq) AS blockers,
                     greatest(o.nb_start,
                              (SELECT max(b.closed_at)
                               FROM   item_link l JOIN item b ON b.id = l.from_item_id
                               WHERE  l.to_item_id = o.id AND l.type = 'blocks'),
                              o.opened_at) AS ready_since
              FROM   open_item o
            )
            SELECT j.id AS "Id",
                   CASE WHEN j.ref_scope_id IS NOT DISTINCT FROM @home OR rs.id IS NULL
                        THEN j.ref ELSE rs.ref || '/' || j.ref END AS "Ref",
                   j.title AS "Title",
                   j.status::text AS "Status",
                   coalesce(to_char(j.due_on, 'YYYY-MM-DD'),
                            to_char(j.due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')) AS "Due",
                   j.due_end AS "DueEnd",
                   h.label AS "HeldBy",
                   j.blockers AS "Blockers",
                   j.ready_since AS "ReadySince",
                   coalesce(j.due_end <= @now, false) AS "IsOverdue",
                   coalesce(j.due_end > @now AND j.due_end <= @untilEnd, false) AS "IsDueBy",
                   (j.status IN ('open','ready')
                    AND cardinality(j.blockers) = 0
                    AND NOT EXISTS (SELECT 1 FROM item c WHERE c.parent_id = j.id
                                      AND c.status NOT IN ('done','cancelled','superseded'))
                    AND coalesce(j.nb_start <= @now, true)
                    AND j.ready_since >= @sinceStart) AS "IsActionable"
            FROM   judged j
            LEFT   JOIN item rs ON rs.id = j.ref_scope_id AND rs.id <> j.id
            LEFT   JOIN LATERAL (
                     SELECT coalesce(hs.name, cc.agent_name) AS label
                     FROM   claim cc LEFT JOIN agent_session hs ON hs.id = cc.session_id
                     WHERE  cc.item_id = j.id AND cc.released_at IS NULL AND cc.expires_at >= @now
                     LIMIT  1) h ON true
            WHERE  j.due_end <= greatest(@untilEnd, @now)
               OR  j.ready_since >= @sinceStart
            ORDER  BY j.due_end NULLS LAST, j.ready_since, j.ref;
            """;

        var hits = await ListAsync<AgendaHit>(sql, new
        {
            scope = scopeItemId,
            home = homeScopeId,
            now,
            untilEnd = bounds.UntilEnd,
            sinceStart = bounds.SinceStart,
        }, cancellationToken);

        static AgendaRow Row(AgendaHit h)
            => new(h.Id, h.Ref, h.Title, h.Status, h.Due, h.DueEnd, h.HeldBy, h.Blockers, h.ReadySince);

        return new Agenda(
            bounds.Zone,
            now,
            bounds.UntilEnd,
            bounds.SinceStart,
            [.. hits.Where(h => h.IsOverdue).Select(Row)],
            [.. hits.Where(h => h.IsDueBy).Select(Row)],
            [.. hits.Where(h => h.IsActionable).OrderBy(h => h.ReadySince).Select(Row)]);
    }
}
