using Dapper;
using Npgsql;
using Workman.Core.Data;

namespace Workman.Api.Auth;

/// <summary>A new feed: the only place its token exists in the clear; the endpoint returns it once and drops it.</summary>
public sealed record CreatedFeed(string Token, DateTimeOffset CreatedAt);

/// <summary>Whose feed a token opens: the user, the workspace it reads, and that workspace's slug.</summary>
public sealed record FeedOwner(Guid UserId, Guid WorkspaceId, string Slug);

/// <summary>
/// Calendar feeds (<c>calendar_feed</c>, doc 02 §12c): one per user per workspace, a long-lived secret in a URL a
/// calendar app polls without headers. Only its SHA-256 is kept (<see cref="LinkToken.Hash"/>).
/// <para>
/// Two connections. The user's own calls — whether a feed exists, a new one, revoking it — come from the dashboard
/// cookie, whose tenant and user the request scope has entered, so they run on <see cref="WorkmanConnections.TenantAsync"/>
/// and the table's tenant policy keeps them in that workspace. Opening a feed by its token comes before any tenant is
/// known — the token is what names it — so it runs as the owner under <see cref="Bypass.Authentication"/>, as a
/// <c>wss_</c> token's lookup does; the feed's items are then read as its user through the tenant path.
/// </para>
/// </summary>
public sealed class CalendarFeedStore(NpgsqlDataSource dataSource)
{
    private readonly WorkmanConnections _connections = new(dataSource);

    /// <summary>When the user's feed in <paramref name="tenant"/> was made; null when they have none.</summary>
    public async Task<DateTimeOffset?> CreatedAtAsync(Guid user, Guid tenant, CancellationToken cancellationToken)
    {
        await using var transaction = await _connections.TenantAsync(cancellationToken);
        return await transaction.Connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            "SELECT created_at FROM calendar_feed WHERE user_id = @user AND workspace_id = @tenant;",
            new { user, tenant },
            transaction.Transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>
    /// A new feed for the user in <paramref name="tenant"/>, replacing the one they had, whose URL stops working. Null
    /// when the user is not a member of that workspace (a system administrator visiting it): a feed belongs to a
    /// membership and ends with it.
    /// </summary>
    public async Task<CreatedFeed?> CreateAsync(Guid user, Guid tenant, CancellationToken cancellationToken)
    {
        var token = LinkToken.New();

        await using var transaction = await _connections.TenantAsync(cancellationToken);
        var createdAt = await transaction.Connection.QuerySingleOrDefaultAsync<DateTimeOffset?>(new CommandDefinition(
            """
            INSERT INTO calendar_feed (id, user_id, workspace_id, token_hash)
            SELECT @id, m.user_id, m.workspace_id, @hash
            FROM   tenant_membership m
            WHERE  m.user_id = @user AND m.workspace_id = @tenant
            ON CONFLICT (user_id, workspace_id) DO UPDATE
               SET id = excluded.id, token_hash = excluded.token_hash, created_at = now()
            RETURNING created_at;
            """,
            new { id = Guid.CreateVersion7(), hash = LinkToken.Hash(token), user, tenant },
            transaction.Transaction,
            cancellationToken: cancellationToken));

        if (createdAt is null)
        {
            return null;
        }

        await transaction.CommitAsync(cancellationToken);
        return new CreatedFeed(token, createdAt.Value);
    }

    /// <summary>Deletes the user's feed in <paramref name="tenant"/>; false when they had none.</summary>
    public async Task<bool> RevokeAsync(Guid user, Guid tenant, CancellationToken cancellationToken)
    {
        await using var transaction = await _connections.TenantAsync(cancellationToken);
        var deleted = await transaction.Connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM calendar_feed WHERE user_id = @user AND workspace_id = @tenant;",
            new { user, tenant },
            transaction.Transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        return deleted == 1;
    }

    /// <summary>
    /// The feed a token opens, as of now: null for a token that is no feed, was replaced or revoked, or whose user is
    /// disabled or no longer a member of the workspace — none of which the caller is told apart.
    /// </summary>
    public async Task<FeedOwner?> OpenAsync(string token, CancellationToken cancellationToken)
    {
        if (!LinkToken.Plausible(token))
        {
            return null;
        }

        await using var connection = await _connections.OwnerBypassAsync(Bypass.Authentication, cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<FeedOwner>(new CommandDefinition(
            """
            SELECT f.user_id AS "UserId", f.workspace_id AS "WorkspaceId", w.slug AS "Slug"
            FROM   calendar_feed f
            JOIN   app_user u ON u.id = f.user_id AND u.disabled_at IS NULL
            JOIN   tenant_membership m ON m.user_id = f.user_id AND m.workspace_id = f.workspace_id
            JOIN   workspace w ON w.id = f.workspace_id
            WHERE  f.token_hash = @hash;
            """,
            new { hash = LinkToken.Hash(token) },
            cancellationToken: cancellationToken));
    }
}
