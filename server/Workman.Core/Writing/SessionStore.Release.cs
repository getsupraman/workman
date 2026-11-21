using System.Text.Json;
using Dapper;
using Workman.Core.Domain;
using Workman.Core.Rendering;

namespace Workman.Core.Writing;

public enum ReleaseOutcome
{
    Released,
    NotFound,
    AlreadyReleased,
    Holding,
}

/// <summary><see cref="Tombstone"/> is the row's name from now on; <see cref="Held"/> the refs that refused it.</summary>
public sealed record ReleaseResult(ReleaseOutcome Outcome, string? Name = null, string? Tombstone = null, string? Held = null);

public sealed partial class SessionStore
{
    /// <summary>The marker a released row's name ends with; its own id makes it unique.</summary>
    public const string TombstoneMarker = "~released~";

    public static string ReleasePath(Guid sessionId) => $"POST /api/agent-sessions/{sessionId}/release";

    public static string TombstoneName(string name, Guid sessionId) => $"{name}{TombstoneMarker}{sessionId:n}";

    private sealed record ReleaseRow(string Name, string? Held);

    /// <summary>
    /// doc 11 — frees a session name for the next mint without deleting the row: claims, events and
    /// evidence keep pointing at it under a tombstone name, its token is revoked and its client key let
    /// go. A session that still holds live claims of its own is refused; revoking it releases them first. A claim
    /// another session took for it (mcp-fit/6) does not refuse: it is released, and its item returns to ready.
    /// </summary>
    public async Task<ReleaseResult> ReleaseNameAsync(
        Guid workspaceId,
        Guid sessionId,
        Actor actor,
        CancellationToken cancellationToken = default)
    {
        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var (connection, transaction) = tenant;

        var row = await connection.QuerySingleOrDefaultAsync<ReleaseRow>(new CommandDefinition(
            """
            SELECT s.name AS "Name",
                   (SELECT string_agg(i.ref, ', ' ORDER BY i.ref)
                    FROM   claim c JOIN item i ON i.id = c.item_id
                    WHERE  c.session_id = s.id AND c.released_at IS NULL AND c.expires_at > now()
                      AND  c.claimed_by IS NULL) AS "Held"
            FROM   agent_session s
            WHERE  s.id = @id AND s.workspace_id = @workspace
            FOR    UPDATE OF s;
            """,
            new { id = sessionId, workspace = workspaceId },
            transaction,
            cancellationToken: cancellationToken));

        if (row is null)
        {
            return new ReleaseResult(ReleaseOutcome.NotFound);
        }

        var tombstone = TombstoneName(row.Name, sessionId);
        if (row.Name.EndsWith($"{TombstoneMarker}{sessionId:n}", StringComparison.Ordinal))
        {
            return new ReleaseResult(ReleaseOutcome.AlreadyReleased, row.Name);
        }

        if (row.Held is { } held)
        {
            return new ReleaseResult(ReleaseOutcome.Holding, row.Name, Held: held);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            WITH released AS (
              UPDATE claim c
                 SET released_at = now(), release_reason = 'session_released'
                FROM item i
               WHERE i.id = c.item_id
                 AND c.session_id = @session
                 AND c.released_at IS NULL
                 AND c.claimed_by IS NOT NULL
              RETURNING c.item_id, i.ref, c.claimed_by
            ),
            returned AS (
              UPDATE item i
                 SET status = 'ready'
                FROM released r
               WHERE i.id = r.item_id AND i.status = 'in_progress'
              RETURNING i.id
            )
            INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary, session_id)
            SELECT @workspace, r.item_id, @actor, @role::actor_role, 'claim_session_released',
                   jsonb_build_object('session', @name::text, 'claimed_by', r.claimed_by),
                   r.ref || ' claim by ' || @name::text || ' (for ' || r.claimed_by || ') released — session released',
                   @session
            FROM   released r;

            UPDATE agent_session
               SET name       = @tombstone,
                   revoked_at = coalesce(revoked_at, now()),
                   client_key = NULL
             WHERE id = @session;

            INSERT INTO event (workspace_id, item_id, actor, role, kind, payload, summary, session_id)
            VALUES (@workspace, NULL, @actor, @role::actor_role, 'session_name_released',
                    @payload::jsonb, @summary, @session);
            """,
            new
            {
                session = sessionId,
                workspace = workspaceId,
                tombstone,
                name = row.Name,
                actor = actor.Name,
                role = Pg.Label(actor.Role),
                payload = JsonSerializer.Serialize(new { name = row.Name, tombstone }),
                summary = Glyph.Flat($"session name {row.Name} released by {actor.Label}; the row is now {tombstone}"),
            },
            transaction,
            cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);

        return new ReleaseResult(ReleaseOutcome.Released, row.Name, tombstone);
    }
}
