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
