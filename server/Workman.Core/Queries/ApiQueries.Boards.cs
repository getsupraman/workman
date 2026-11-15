using Dapper;
using Workman.Core.Domain;

namespace Workman.Core.Queries;

/// <summary>A board row under one of the roots <see cref="ApiQueries.BoardsAsync"/> was asked for.</summary>
public sealed record RootedBoardRow(Guid Root, BoardRow Row);

/// <summary>
/// One row of <see cref="ApiQueries.BarsAsync"/>: the root itself, a counted item (<see cref="Counted"/>) or a claimed
/// one, with the inputs the dashboard's <c>state()</c> takes that the item's ref does not carry. <see cref="Proof"/> is
/// the latest verification's: passed, and run by someone other than the worker.
/// </summary>
public sealed record BarRow(
    Guid Root,
    Guid Id,
    string Kind,
    ItemStatus Status,
    ItemResolution? Resolution,
    DateTimeOffset? ClosedAt,
    bool Counted,
    bool Proof);

public sealed partial class ApiQueries
{
    private sealed record RootKey(Guid Root);

    /// <summary>
    /// <see cref="BoardAsync"/> for many roots in one command: each root's rows in its pre-order, the roots in the
    /// order given. A row under two of the roots is returned under each.
    /// </summary>
    public async Task<IReadOnlyList<RootedBoardRow>> BoardsAsync(
        IReadOnlyList<Guid> roots,
        CancellationToken cancellationToken = default)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        const string sql =
            $"""
             SELECT t.root AS "Root",
                    {BoardColumns}
             FROM   unnest(@roots) WITH ORDINALITY AS t(root, n)
             JOIN   item i ON i.id = t.root OR i.ancestors @> ARRAY[t.root]
             {BoardJoins}
             ORDER  BY t.n, p.path || i.seq, i.id;
             """;

        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var rows = await tenant.Connection.QueryAsync<RootKey, BoardRow, RootedBoardRow>(
            new CommandDefinition(sql, new { roots = roots.ToArray() }, cancellationToken: cancellationToken),
            (key, row) => new RootedBoardRow(key.Root, row),
            splitOn: "Id");
        return rows.AsList();
    }

    /// <summary>
    /// What the dashboard's bars draw for each root, in one command whatever the number of roots: the root's own row,
    /// then in pre-order every item under it that is counted (a leaf kind, or a phase or feature with nothing below —
    /// <see cref="WorkmanQueries.Counted"/>, and the dashboard's <c>counts()</c>) or that <paramref name="claimed"/>
    /// names.
    /// </summary>
    public async Task<IReadOnlyList<BarRow>> BarsAsync(
        IReadOnlyList<Guid> roots,
        IReadOnlyCollection<Guid> claimed,
        CancellationToken cancellationToken = default)
    {
        if (roots.Count == 0)
        {
            return [];
        }

        const string counted =
            $"(i.kind IN {LeafKinds} OR (i.kind IN ('phase','feature') AND NOT EXISTS (SELECT 1 FROM item g WHERE g.parent_id = i.id)))";

        const string sql =
            $"""
             SELECT t.root       AS "Root",
                    i.id         AS "Id",
                    i.kind       AS "Kind",
                    i.status     AS "Status",
                    i.resolution AS "Resolution",
                    i.closed_at  AS "ClosedAt",
                    {counted}    AS "Counted",
                    coalesce(v.passed AND v.run_by <> 'worker', false) AS "Proof"
             FROM   unnest(@roots) WITH ORDINALITY AS t(root, n)
             JOIN   item i ON i.id = t.root OR i.ancestors @> ARRAY[t.root]
             {WorkmanQueries.DocumentPath}
             LEFT   JOIN LATERAL (
                      SELECT vv.passed, vv.run_by FROM verification vv
                      WHERE  vv.item_id = i.id
                      ORDER  BY vv.at DESC, vv.id DESC LIMIT 1) v ON true
             WHERE  i.id = t.root OR i.id = ANY(@claimed) OR {counted}
             ORDER  BY t.n, p.path || i.seq, i.id;
             """;

        return await ListAsync<BarRow>(
            sql,
            new { roots = roots.ToArray(), claimed = claimed.ToArray() },
            cancellationToken);
    }
}
