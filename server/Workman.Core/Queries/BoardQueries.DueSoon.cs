namespace Workman.Core.Queries;

/// <summary>
/// One open item with a due that has passed or falls within the window. <see cref="Ref"/> is bare inside the home
/// scope and qualified outside it, as on the agenda; <see cref="QualifiedRef"/> always carries its ref scope.
/// <see cref="DaysLeft"/> counts calendar days in the workspace's zone from today to the due's day: 0 is today,
/// negative is a day already gone. <see cref="Overdue"/> is the one evaluation (<c>item_due_end(i) &lt;= now</c>),
/// so an instant due earlier today is overdue with 0 days left.
/// </summary>
public sealed record DueSoonRow(
    Guid Id,
    string Ref,
    string QualifiedRef,
    string Title,
    string Due,
    DateTimeOffset DueEnd,
    int DaysLeft,
    bool Overdue,
    string[] Blockers);

/// <summary>Today in the workspace's zone, with the zone's name — what the dates block prints and links on.</summary>
public sealed record ZoneToday(string Zone, string Day)
{
    public DateOnly Today => DateOnly.ParseExact(Day, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed partial class BoardQueries
{
    /// <summary>
    /// Open items under <paramref name="scopeItemId"/> that are overdue or due within <paramref name="days"/> days
    /// of today in the workspace's zone, earliest due first. Only dated items are evaluated, so an undated scope
    /// costs one indexed scan and returns nothing. Blockers are the unfinished <c>blocks</c> blockers.
    /// </summary>
    public async Task<IReadOnlyList<DueSoonRow>> DueSoonAsync(
        Guid scopeItemId,
        Guid? homeScopeId,
        DateTimeOffset now,
        int days,
        CancellationToken cancellationToken = default)
        => [.. (await DueSoonAsync([scopeItemId], homeScopeId, now, days, cancellationToken))[scopeItemId]];

    /// <summary>
    /// <see cref="DueSoonAsync(Guid, Guid?, DateTimeOffset, int, CancellationToken)"/> for many scopes in one
    /// command, each ref printed against the one <paramref name="homeScopeId"/>.
    /// </summary>
    public async Task<ILookup<Guid, DueSoonRow>> DueSoonAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        Guid? homeScopeId,
        DateTimeOffset now,
        int days,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
            WITH dated AS (
              SELECT i.id, i.ref, i.ref_scope_id, i.title, i.due_on, i.due_at,
                     item_due_end(i) AS due_end,
                     timezone_of(i.workspace_id) AS zone,
                     sc.id AS scope_id
              FROM   item i
              {Scoped.JoinItem}
              WHERE  i.status NOT IN ('done','cancelled','superseded')
                AND  (i.due_on IS NOT NULL OR i.due_at IS NOT NULL)
            ),
            dayed AS (
              SELECT d.*,
                     coalesce(d.due_on, (d.due_at AT TIME ZONE d.zone)::date)
                       - (@now AT TIME ZONE d.zone)::date AS days_left
              FROM   dated d
            )
            SELECT d.id AS "Id",
                   CASE WHEN d.ref_scope_id IS NOT DISTINCT FROM @home OR rs.id IS NULL
                        THEN d.ref ELSE rs.ref || '/' || d.ref END AS "Ref",
                   CASE WHEN rs.id IS NULL THEN d.ref ELSE rs.ref || '/' || d.ref END AS "QualifiedRef",
                   d.title AS "Title",
                   coalesce(to_char(d.due_on, 'YYYY-MM-DD'),
                            to_char(d.due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')) AS "Due",
                   d.due_end AS "DueEnd",
                   d.days_left AS "DaysLeft",
                   d.due_end <= @now AS "Overdue",
                   ARRAY(SELECT CASE WHEN b.ref_scope_id IS NOT DISTINCT FROM @home OR bs.id IS NULL
                                     THEN b.ref ELSE bs.ref || '/' || b.ref END
                         FROM   item_link l
                         JOIN   item b ON b.id = l.from_item_id
                         LEFT   JOIN item bs ON bs.id = b.ref_scope_id AND bs.id <> b.id
                         WHERE  l.to_item_id = d.id AND l.type = 'blocks'
                           AND  b.status NOT IN ('done','cancelled','superseded')
                         ORDER  BY b.seq) AS "Blockers",
                   d.scope_id AS "ForScope"
            FROM   dayed d
            LEFT   JOIN item rs ON rs.id = d.ref_scope_id AND rs.id <> d.id
            WHERE  d.due_end <= @now OR d.days_left <= @days
            ORDER  BY d.due_end, d.ref;
            """;

        return await ScopedAsync<DueSoonRow>(
            sql,
            new { scopes = Scoped.Ids(scopeItemIds), home = homeScopeId, now, days },
            cancellationToken);
    }

    /// <summary>The workspace's zone and today's date there, as of <paramref name="now"/>.</summary>
    public async Task<ZoneToday> ZoneTodayAsync(
        Guid workspaceId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT z.zone AS "Zone", to_char((@now AT TIME ZONE z.zone)::date, 'YYYY-MM-DD') AS "Day"
            FROM   (SELECT timezone_of(@workspace) AS zone) z;
            """;

        return (await ListAsync<ZoneToday>(sql, new { workspace = workspaceId, now }, cancellationToken))[0];
    }

    /// <summary>
    /// doc 01 §5 <c>due-after-parent</c>: an open child whose due ends after its parent's due ends, both dated.
    /// Compared as instants through <c>item_due_end</c>, so a day and a date-time are judged in the zone.
    /// </summary>
    public async Task<IReadOnlyList<AuditRow>> DueAfterParentAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default,
        Guid? home = null)
    {
        var sql =
            $"""
             SELECT 'due-after-parent' AS "Check",
                    {ShownRef("i")}    AS "Ref",
                    'due ' || coalesce(to_char(i.due_on, 'YYYY-MM-DD'),
                                       to_char(i.due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))
                      || ' ends after its parent ' || {ShownRef("p")} || ' is due ('
                      || coalesce(to_char(p.due_on, 'YYYY-MM-DD'),
                                  to_char(p.due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"'))
                      || ')' AS "Problem",
                    'work_update(ref:"' || {ShownRef("i")} || '", due:"…") to bring it inside, or move '
                      || {ShownRef("p")} || '''s due' AS "Route"
             FROM   item i
             JOIN   item p ON p.id = i.parent_id
             WHERE  {InScope}
               AND  i.status NOT IN ('done','cancelled','superseded')
               AND  (i.due_on IS NOT NULL OR i.due_at IS NOT NULL)
               AND  (p.due_on IS NOT NULL OR p.due_at IS NOT NULL)
               AND  item_due_end(i) > item_due_end(p)
             ORDER  BY i.ref;
             """;

        return await ListAsync<AuditRow>(sql, new { scope = scopeItemId, home }, cancellationToken);
    }
}
