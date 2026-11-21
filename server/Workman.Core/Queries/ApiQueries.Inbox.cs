namespace Workman.Core.Queries;

/// <summary>mcp-fit/7: a <c>blocked</c> item an open <c>wait</c> holds, with the wait whose due comes first.</summary>
public sealed record WaitedRow(Guid Id, DateTimeOffset Since, Guid WaitId);

public sealed partial class ApiQueries
{
    /// <summary>mcp-fit/7: the open <c>wait</c> items under the scopes — the inbox's waiting group.</summary>
    public async Task<ILookup<Guid, WaitingRow>> OpenWaitsAsync(
        IReadOnlyCollection<Guid> scopeIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.id AS "Id", i.opened_at AS "Since", {Scoped.Column}
             FROM   item i
             {Scoped.JoinItem}
             WHERE  i.kind = 'wait' AND i.status NOT IN {Closed}
             ORDER  BY i.seq, i.id;
             """;

        return await Scoped.ListAsync<WaitingRow>(_connections, sql, new { scopes = Scoped.Ids(scopeIds) }, cancellationToken);
    }

    /// <summary>
    /// mcp-fit/7: <c>blocked</c> items an open <c>wait</c> holds through a <c>blocks</c> edge — the ones
    /// <see cref="BlockedOnAPersonAsync(IReadOnlyCollection{Guid}, CancellationToken)"/> leaves out because something open
    /// blocks them, when that something is someone else's answer. <c>WaitId</c> is the blocking wait due first.
    /// </summary>
    public async Task<ILookup<Guid, WaitedRow>> BlockedOnAWaitAsync(
        IReadOnlyCollection<Guid> scopeIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.id AS "Id",
                    coalesce((SELECT max(e.at) FROM event e
                              WHERE e.item_id = i.id AND e.kind IN ('status','forced_transition')),
                             i.started_at, i.opened_at) AS "Since",
                    w.id AS "WaitId",
                    {Scoped.Column}
             FROM   item i
             {Scoped.JoinItem}
             JOIN   LATERAL (SELECT b.id FROM item_link l JOIN item b ON b.id = l.from_item_id
                             WHERE  l.to_item_id = i.id AND l.type = 'blocks'
                               AND  b.kind = 'wait' AND b.status NOT IN {Closed}
                             ORDER  BY item_due_end(b) NULLS LAST, b.seq, b.id
                             LIMIT  1) w ON true
             WHERE  i.status = 'blocked'
             ORDER  BY i.seq, i.id;
             """;

        return await Scoped.ListAsync<WaitedRow>(_connections, sql, new { scopes = Scoped.Ids(scopeIds) }, cancellationToken);
    }
}
