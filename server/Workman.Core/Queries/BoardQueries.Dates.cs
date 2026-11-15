namespace Workman.Core.Queries;

/// <summary>
/// An item's due and not_before as work_get prints them: each in its written form (a day, or an instant in UTC), the
/// workspace's zone, the instants the <c>ItemDates</c> SQL functions read them as, and the due's slips — the
/// <c>dates_changed</c> events that moved it later.
/// </summary>
public sealed record ItemDatesRow(
    Guid ItemId,
    string? Due,
    bool DueIsDay,
    string? NotBefore,
    bool NotBeforeIsDay,
    string Zone,
    DateTimeOffset? DueEnd,
    DateTimeOffset? NotBeforeStart,
    DateTimeOffset? ClosedAt,
    bool Done,
    long Slips)
{
    /// <summary>Closed as done after its due had passed.</summary>
    public bool Late => Done && ClosedAt is { } closed && DueEnd is { } end && closed >= end;
}

public sealed partial class BoardQueries
{
    /// <summary>
    /// A row only for an item that has a date or has had one, so an item that never carried a date reads exactly as
    /// it did before dates existed.
    /// </summary>
    public async Task<IReadOnlyList<ItemDatesRow>> DatesAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT i.id AS "ItemId",
                   coalesce(to_char(i.due_on, 'YYYY-MM-DD'),
                            to_char(i.due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')) AS "Due",
                   i.due_on IS NOT NULL AS "DueIsDay",
                   coalesce(to_char(i.not_before_on, 'YYYY-MM-DD'),
                            to_char(i.not_before_at AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS"Z"')) AS "NotBefore",
                   i.not_before_on IS NOT NULL AS "NotBeforeIsDay",
                   timezone_of(i.workspace_id) AS "Zone",
                   item_due_end(i) AS "DueEnd",
                   item_not_before_start(i) AS "NotBeforeStart",
                   i.closed_at AS "ClosedAt",
                   i.status = 'done' AS "Done",
                   (SELECT count(*)
                      FROM event e
                     WHERE e.item_id = i.id
                       AND e.kind = 'dates_changed'
                       AND e.payload->>'field' = 'due'
                       AND date_text_end(e.payload->>'to', timezone_of(i.workspace_id))
                           > date_text_end(e.payload->>'from', timezone_of(i.workspace_id))) AS "Slips"
            FROM   item i
            WHERE  i.id = ANY(@ids)
              AND  (i.due_on IS NOT NULL OR i.due_at IS NOT NULL
                    OR i.not_before_on IS NOT NULL OR i.not_before_at IS NOT NULL
                    OR EXISTS (SELECT 1 FROM event e WHERE e.item_id = i.id AND e.kind = 'dates_changed'));
            """;

        return await ListAsync<ItemDatesRow>(sql, new { ids = itemIds.ToArray() }, cancellationToken);
    }
}
