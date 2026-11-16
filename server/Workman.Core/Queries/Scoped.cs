using Dapper;
using Workman.Core.Data;

namespace Workman.Core.Queries;

/// <summary>
/// A read over many scopes at once. Its SQL joins <see cref="Scopes"/> and ends its select list with
/// <c>sc.id AS "ForScope"</c>, so each row comes back once for every scope it sits under — what one call per scope
/// returned, in one command. The one-scope methods call these with one id, so the MCP tools and the v1 API read
/// the same predicate.
/// </summary>
internal static class Scoped
{
    /// <summary>The scopes, as <c>sc(id)</c>; the query passes them as <c>@scopes</c>.</summary>
    public const string Scopes = "unnest(@scopes::uuid[]) AS sc(id)";

    /// <summary>Joins <see cref="Scopes"/> to <c>i</c>: <c>i</c> is the scope or anything beneath it.</summary>
    public const string JoinItem = $"JOIN {Scopes} ON (i.id = sc.id OR i.ancestors @> ARRAY[sc.id]::uuid[])";

    /// <summary>The last column of every scoped select.</summary>
    public const string Column = "sc.id AS \"ForScope\"";

    private sealed record Tag(Guid ForScope);

    public static async Task<ILookup<Guid, T>> ListAsync<T>(
        WorkmanConnections connections,
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        await using var tenant = await connections.TenantAsync(cancellationToken);
        var rows = await tenant.Connection.QueryAsync<T, Tag, (Guid Scope, T Row)>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken),
            (row, tag) => (tag.ForScope, row),
            splitOn: "ForScope");
        return rows.ToLookup(r => r.Scope, r => r.Row);
    }

    public static Guid[] Ids(IEnumerable<Guid> scopes) => [.. scopes.Distinct()];
}

/// <summary>A count for one scope of a scoped read.</summary>
public sealed record ScopeCount(Guid Scope, int Count);
