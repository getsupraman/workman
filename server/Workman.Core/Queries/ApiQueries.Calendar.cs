using Workman.Core.Writing;

namespace Workman.Core.Queries;

public sealed partial class ApiQueries
{
    /// <summary>
    /// Every open item of the workspace that has a due, soonest first, as the caller's tenant connection sees them:
    /// read as a user, the projects hidden from them are not there (doc 02 §15b). The due is read in its stored
    /// form, a day or an instant, never turned into the other here; its order is <c>item_due_end</c>'s. An archived
    /// project's items are left out, as every default list leaves the project out (doc 02 "Archived projects").
    /// </summary>
    public Task<IReadOnlyList<CalendarRow>> CalendarAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => ListAsync<CalendarRow>(
            """
            SELECT i.id     AS "Id",
                   i.status AS "Status",
                   to_char(i.due_on, 'YYYYMMDD')     AS "Day",
                   to_char(i.due_on + 1, 'YYYYMMDD') AS "DayAfter",
                   to_char(i.due_at AT TIME ZONE 'UTC', 'YYYYMMDD"T"HH24MISS"Z"') AS "At"
            FROM   item i
            WHERE  i.workspace_id = @workspace
              AND  i.status IN ('open','ready','in_progress','blocked','review')
              AND  (i.due_on IS NOT NULL OR i.due_at IS NOT NULL)
              AND  NOT EXISTS (SELECT 1 FROM item ap
                               WHERE  (ap.id = i.id OR ap.id = ANY(i.ancestors))
                                 AND  ap.archived_at IS NOT NULL)
            ORDER  BY item_due_end(i), i.id;
            """,
            new { workspace = workspaceId },
            cancellationToken);

    /// <summary>The workspace's time zone, as the date functions read it (<c>timezone_of</c>).</summary>
    public async Task<string> TimeZoneAsync(Guid workspaceId, CancellationToken cancellationToken = default)
        => await SingleAsync<string>("SELECT timezone_of(@workspace);", new { workspace = workspaceId }, cancellationToken) ?? "UTC";
}

/// <summary>
/// The instants a calendar range was read as, in the workspace's zone (<c>timezone_of</c>). <see cref="ToStart"/> is where
/// <c>to</c> begins (a day's start), which a valid range never puts before <see cref="FromStart"/>.
/// </summary>
public sealed record CalendarRangeBounds(string Zone, DateTimeOffset FromStart, DateTimeOffset ToStart, DateTimeOffset ToEnd);

/// <summary>
/// One dated item of a calendar range, open or closed. <see cref="Due"/> and <see cref="NotBefore"/> are the written form
/// (a day, or an instant in UTC); <see cref="BlockerIds"/> are its unfinished <c>blocks</c> blockers, in seq order.
/// </summary>
public sealed record CalendarRangeRow(
    Guid Id,
    string Kind,
    string Status,
    string? Due,
    string? NotBefore,
    DateTimeOffset? DueEnd,
    DateTimeOffset? NotBeforeStart,
    bool Late,
    DateTimeOffset? ClosedAt,
    string? HeldBy,
    string[] BlockerIds);

public sealed partial class ApiQueries
{
    /// <summary>
    /// The instants <paramref name="from"/> and <paramref name="to"/> are read as: a day <paramref name="from"/> from its
    /// start, a day <paramref name="to"/> to its end, both in the workspace's zone (<c>date_value_start/end</c>).
    /// </summary>
    public async Task<CalendarRangeBounds> CalendarBoundsAsync(
        Guid workspaceId,
        DateValue from,
        DateValue to,
        CancellationToken cancellationToken = default)
        => (await ListAsync<CalendarRangeBounds>(
            """
            SELECT z.zone AS "Zone",
                   date_value_start(@fromDay::date, @fromAt, z.zone) AS "FromStart",
                   date_value_start(@toDay::date, @toAt, z.zone)     AS "ToStart",
                   date_value_end(@toDay::date, @toAt, z.zone)       AS "ToEnd"
            FROM   (SELECT timezone_of(@workspace) AS zone) z;
            """,
            new
            {
                workspace = workspaceId,
                fromDay = from.Day?.ToDateTime(TimeOnly.MinValue),
                fromAt = from.At,
                toDay = to.Day?.ToDateTime(TimeOnly.MinValue),
                toAt = to.At,
            },
            cancellationToken))[0];

    /// <summary>
    /// Every item, open or done, whose due or not_before falls in [<see cref="CalendarRangeBounds.FromStart"/>,
    /// <see cref="CalendarRangeBounds.ToEnd"/>], as the caller's tenant connection sees them (the projects hidden from
    /// them are not there, doc 02 §15b); cancelled and superseded items and an archived project's items are left out.
    /// A day is in when it is one of the range's days in the workspace's zone, an instant when it is inside the range
    /// (a day <c>to</c> ends at the next midnight, which belongs to the next day). With <paramref name="scopeItemId"/>,
    /// only that item's subtree. Ordered by the earlier of its due end and its not_before start.
    /// </summary>
    public Task<IReadOnlyList<CalendarRangeRow>> CalendarRangeAsync(
        Guid workspaceId,
        Guid? scopeItemId,
        CalendarRangeBounds bounds,
        bool toIsInstant,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
        => ListAsync<CalendarRangeRow>(
            """
            WITH r AS (
              SELECT (@fromStart::timestamptz AT TIME ZONE @zone)::date                         AS first_day,
                     ((@toEnd::timestamptz - interval '1 microsecond') AT TIME ZONE @zone)::date AS last_day
            ),
            dated AS (
              SELECT i.*, item_due_end(i) AS due_end, item_not_before_start(i) AS nb_start
              FROM   item i
              WHERE  i.workspace_id = @workspace
                AND  i.status NOT IN ('cancelled','superseded')
                AND  (@scope::uuid IS NULL OR i.id = @scope OR i.ancestors @> ARRAY[@scope]::uuid[])
                AND  NOT EXISTS (SELECT 1 FROM item ap
                                 WHERE  (ap.id = i.id OR ap.id = ANY(i.ancestors))
                                   AND  ap.archived_at IS NOT NULL)
            )
            SELECT d.id            AS "Id",
                   d.kind::text    AS "Kind",
                   d.status::text  AS "Status",
                   coalesce(to_char(d.due_on, 'YYYY-MM-DD'),
                            to_char(d.due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')) AS "Due",
                   coalesce(to_char(d.not_before_on, 'YYYY-MM-DD'),
                            to_char(d.not_before_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')) AS "NotBefore",
                   d.due_end       AS "DueEnd",
                   d.nb_start      AS "NotBeforeStart",
                   coalesce(d.status = 'done' AND d.closed_at >= d.due_end, false) AS "Late",
                   CASE WHEN d.status IN ('done','cancelled','superseded') THEN d.closed_at END AS "ClosedAt",
                   h.label         AS "HeldBy",
                   ARRAY(SELECT b.id::text
                         FROM   item_link l
                         JOIN   item b ON b.id = l.from_item_id
                         WHERE  l.to_item_id = d.id AND l.type = 'blocks'
                           AND  b.status NOT IN ('done','cancelled','superseded')
                         ORDER  BY b.seq) AS "BlockerIds"
            FROM   dated d
            CROSS  JOIN r
            LEFT   JOIN LATERAL (
                     SELECT coalesce(hs.name, cc.agent_name) AS label
                     FROM   claim cc LEFT JOIN agent_session hs ON hs.id = cc.session_id
                     WHERE  cc.item_id = d.id AND cc.released_at IS NULL AND cc.expires_at >= @now
                     LIMIT  1) h ON true
            WHERE  d.due_on BETWEEN r.first_day AND r.last_day
               OR  d.not_before_on BETWEEN r.first_day AND r.last_day
               OR  (d.due_at >= @fromStart AND (d.due_at < @toEnd OR (@toIsInstant AND d.due_at = @toEnd)))
               OR  (d.not_before_at >= @fromStart AND (d.not_before_at < @toEnd OR (@toIsInstant AND d.not_before_at = @toEnd)))
            ORDER  BY least(d.due_end, d.nb_start), d.seq, d.id;
            """,
            new
            {
                workspace = workspaceId,
                scope = scopeItemId,
                zone = bounds.Zone,
                fromStart = bounds.FromStart,
                toEnd = bounds.ToEnd,
                toIsInstant,
                now,
            },
            cancellationToken);
}
