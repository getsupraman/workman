using System.Data;
using Dapper;
using Npgsql;
using Workman.Core.Domain;
using Workman.Core.Rendering;

namespace Workman.Core.Writing;

/// <summary>
/// doc 07 §5. <c>in_progress</c> means someone is working on this <em>right now</em>, and the claim
/// is what makes that falsifiable. Two Postgres guarantees carry it — one live claim per item, one
/// live write claim per <c>serialization_key</c> — so this layer's job is the message and the lease,
/// not the exclusion.
/// </summary>
public sealed partial class WorkWriter
{
    private sealed record Predecessor(string Ref, ItemStatus Status);

    /// <summary>What <c>claim_one_writer_per_tree</c> is keyed on, and the tree the caller named.</summary>
    private sealed record TreeLock(string Key, Guid Workspace, Guid? User, string? Tree);

    private sealed record KeySources(string? RunKey, string? RepoRoot, string? ScopeRef);

    /// <summary>
    /// Refuses a second claim, and says who holds the first. Two runs in different worktrees proceed
    /// concurrently; two sharing a working tree do not — that is the whole parallel-orchestrator
    /// rule, and the refusal names the call that gets on with something else instead.
    /// <para>
    /// Every unreleased claim that <c>claim_one_live_per_item</c>, <c>claim_one_writer_per_tree</c>
    /// or <c>claim_one_session_per_tree</c> would collide with is read here, lapsed-but-unswept ones
    /// too, since a constraint does not know about leases. Two things pass: the holder re-taking its
    /// own item, which extends the lease, and the session that holds a tree claiming another item of
    /// it (doc 11). A claim with no session — one from before doc 11 — still holds its tree alone.
    /// </para>
    /// </summary>
    private async Task<string?> ClaimRefusalAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        StatusArgs args,
        CancellationToken cancellationToken)
    {
        var holder = args.Holder ?? actor;
        var treeLock = await TreeLockAsync(connection, transaction, item, actor, args.Tree, cancellationToken);
        var retake = Retake(item.Ref, treeLock.Tree);

        const string sql =
            """
            SELECT c.item_id           AS "ItemId",
                   c.agent_name        AS "AgentName",
                   c.serialization_key AS "SerializationKey",
                   c.expires_at        AS "ExpiresAt",
                   c.heartbeat_at      AS "HeartbeatAt",
                   i.ref               AS "ItemRef",
                   CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                                       AS "QualifiedRef",
                   c.session_id        AS "SessionId",
                   c.holder_user_id    AS "HolderUserId",
                   coalesce(hs.name, c.agent_name) AS "Holder",
                   c.claimed_by_session_id AS "ClaimedBySessionId",
                   c.claimed_by        AS "ClaimedBy"
            FROM   claim c
            JOIN   item i ON i.id = c.item_id
            LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
            LEFT   JOIN agent_session hs ON hs.id = c.session_id
            WHERE  c.released_at IS NULL
              AND  (c.item_id = @id
                    OR (c.writes AND c.serialization_key = @key
                        AND c.key_workspace_id = @keyWorkspace
                        AND c.key_user_id IS NOT DISTINCT FROM @keyUser))
            ORDER  BY (c.item_id = @id) DESC, c.acquired_at;
            """;

        var held = (await connection.QueryAsync<ClaimHolder>(new CommandDefinition(
            sql,
            new { id = item.Id, key = treeLock.Key, keyWorkspace = treeLock.Workspace, keyUser = treeLock.User },
            transaction,
            cancellationToken: cancellationToken))).AsList();

        var live = held.FirstOrDefault(h => h.ItemId == item.Id ? !Retakes(h, actor, holder) : !SameSession(h, holder));
        if (live is null)
        {
            return null;
        }

        var you = live.SessionId is null && actor.SessionId is null && SameHolder(live, actor)
            ? " — that is you"
            : string.Empty;

        if (live.ItemId == item.Id)
        {
            return Refusal.Blocked(
                $"work_status({item.Ref} → in_progress). {item.Ref} is already claimed.",
                $"{HolderText(live)} holds a live claim on {item.Ref}; {Lease(live)}",
                "work_next() for something else that is ready",
                $"{retake} once that lease lapses — "
                + "the sweep releases it and returns the item to ready");
        }

        var routes = new List<string>
        {
            $"work_status(ref:\"{live.QualifiedRef}\", to:\"review\") to release it — any move out of "
            + "in_progress (review, blocked, done) releases the claim",
            $"{retake} once that lease lapses — the sweep releases it",
        };

        if (treeLock.Tree is null)
        {
            routes.Add($"work_status(ref:\"{item.Ref}\", to:\"in_progress\", tree:\"<your worktree path>\") "
                       + "if you work in a git worktree of your own — one writer per tree, many per run");
        }

        routes.Add("work_next(scope:\"<another run>\") — two runs in different worktrees proceed in "
                   + "parallel, two sharing one working tree do not");

        return Refusal.Blocked(
            $"work_status({item.Ref} → in_progress). Another writer holds this working tree.",
            $"{live.QualifiedRef} holds the write claim on \"{Glyph.Flat(live.SerializationKey)}\" "
            + $"(held by {HolderText(live)}{you}); {Lease(live)}",
            [.. routes]);
    }

    /// <summary>
    /// The holder re-taking its own item: the same session, or the same user and name where the claim has none. A claim
    /// taken for a session (mcp-fit/6) is also re-taken by the session that claimed it, and re-claimed for its holder.
    /// </summary>
    private static bool Retakes(ClaimHolder held, Actor actor, Actor holder)
        => held.SessionId is { } session
            ? session == actor.SessionId
              || session == holder.SessionId
              || (held.ClaimedBySessionId is { } claimer && claimer == actor.SessionId)
            : SameHolder(held, actor);

    /// <summary>The holder as surfaces print it: <c>wk-a (for ctl)</c> for a claim taken for a session.</summary>
    private static string HolderText(ClaimHolder held)
        => held.ClaimedBy is { Length: > 0 } by ? $"{held.Holder} (for {by})" : held.Holder;

    /// <summary>The longest lease an orchestrator may set (C346); the default stays the workspace's.</summary>
    internal const int MaxLeaseMinutes = 480;

    /// <summary>
    /// mcp-fit/6 — <c>lease</c> and <c>for_session</c>, refused before anything is opened: they travel with
    /// <c>in_progress</c> only, an orchestrator, controller or human sets them (C344), and the lease is capped.
    /// </summary>
    private static string? DelegationRefusal(string reference, Move move, Actor actor, StatusArgs args)
    {
        if (!args.Delegates)
        {
            return null;
        }

        var plain = $"work_status(ref:\"{reference}\", to:\"in_progress\")";

        if (move != Move.InProgress)
        {
            return Refusal.Blocked(
                $"work_status({reference} → {Transitions.Name(move)}) with lease or for_session. Nothing was written.",
                "lease and for_session only travel with to:\"in_progress\" — they shape the claim it takes",
                $"work_status(ref:\"{reference}\", to:\"{Transitions.Name(move)}\") without them");
        }

        if (actor.Role is not (ActorRole.Orchestrator or ActorRole.Controller or ActorRole.Human))
        {
            return Refusal.Blocked(
                $"work_status({reference} → in_progress) with lease or for_session from a {Pg.Label(actor.Role)}. "
                + "Nothing was written.",
                "only an orchestrator sets a lease or claims for another session; your own claim takes the "
                + "workspace's lease and renews on every call you make",
                $"{plain} without them",
                "work_report(…) to hand back, and ask your orchestrator for a longer lease");
        }

        if (args.Lease is { } lease && lease is < 1 or > MaxLeaseMinutes)
        {
            return Refusal.Blocked(
                $"work_status({reference} → in_progress, lease:{lease}). Nothing was written.",
                $"lease is 1..{MaxLeaseMinutes} minutes; without it the workspace's default applies, and any call "
                + "of the holding session renews it",
                $"work_status(ref:\"{reference}\", to:\"in_progress\", lease:{MaxLeaseMinutes}{For(args)})");
        }

        return null;
    }

    private static string For(StatusArgs args)
        => string.IsNullOrWhiteSpace(args.ForSession) ? string.Empty : $", for_session:\"{args.ForSession!.Trim()}\"";

    private sealed record HolderRow(Guid Id, string Name);

    /// <summary>
    /// The session <c>for_session</c> names: a live one of this workspace and of the caller's user, by the name it was
    /// launched under. A released name is a tombstone and a revoked session holds nothing, so both are refused.
    /// </summary>
    private static async Task<(Actor? Holder, string? Refused)> HolderAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        string name,
        CancellationToken cancellationToken)
    {
        var wanted = name.Trim();
        var row = await connection.QuerySingleOrDefaultAsync<HolderRow>(new CommandDefinition(
            """
            SELECT id AS "Id", name AS "Name"
            FROM   agent_session
            WHERE  workspace_id = @workspace AND user_id IS NOT DISTINCT FROM @user AND name = @name
              AND  revoked_at IS NULL
            FOR    UPDATE;
            """,
            new { workspace = item.WorkspaceId, user = actor.UserId, name = wanted },
            transaction,
            cancellationToken: cancellationToken));

        if (row is null)
        {
            return (null, Refusal.Blocked(
                $"work_status({item.Ref} → in_progress, for_session:\"{Glyph.Flat(wanted)}\"). Nothing was written.",
                $"no live session is named \"{Glyph.Flat(wanted)}\" in this workspace — it was never launched, or "
                + "it was released or stopped",
                "agent_list() for the names of the live sessions, then the same call with one of them",
                $"work_status(ref:\"{item.Ref}\", to:\"in_progress\") to hold it yourself"));
        }

        return (actor with { SessionId = row.Id, Session = row.Name }, null);
    }

    /// <summary>
    /// Users share role names, so a name alone is not a holder: a claim with no user is the caller's only when the caller
    /// has none either — a static or local token, as before users.
    /// </summary>
    private static bool SameHolder(ClaimHolder holder, Actor actor)
        => holder.AgentName == actor.Name && holder.HolderUserId == actor.UserId;

    /// <summary>
    /// doc 11 — one writer per tree is one session per tree. Only a session can share a tree with
    /// itself; a claim from before sessions holds its tree alone, as it always did.
    /// </summary>
    private static bool SameSession(ClaimHolder holder, Actor actor)
        => holder.SessionId is { } session && session == actor.SessionId;

    private static string Retake(string reference, string? tree)
        => tree is null
            ? $"work_status(ref:\"{reference}\", to:\"in_progress\")"
            : $"work_status(ref:\"{reference}\", to:\"in_progress\", tree:\"{tree}\")";

    private static string Lease(ClaimHolder holder)
    {
        var at = $"{holder.ExpiresAt.UtcDateTime:HH:mm} UTC";
        var left = holder.ExpiresAt - DateTimeOffset.UtcNow;

        return left > TimeSpan.Zero
            ? $"the lease lapses in {Glyph.Age(left)} ({at})"
            : $"the lease lapsed {Glyph.Ago(-left)} ({at}) and is not swept yet";
    }

    private async Task<string> AcquireAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        StatusArgs args,
        CancellationToken cancellationToken)
    {
        var holder = args.Holder ?? actor;
        var delegated = args.Holder is not null;
        var lease = args.Lease;
        var ttl = lease ?? await TtlAsync(connection, transaction, item.WorkspaceId, cancellationToken);
        var treeLock = await TreeLockAsync(connection, transaction, item, actor, args.Tree, cancellationToken);
        var label = delegated ? $"{holder.Label} (for {actor.Label})" : actor.Label;

        // A plain re-take by the holder keeps the claim as it is, lease and claimer included; anything else re-shapes it:
        // a claim for a session, a lease, or the claimer taking its item back, who is then the holder.
        const string extend =
            """
            UPDATE claim
               SET heartbeat_at          = now(),
                   expires_at            = now() + make_interval(mins => CASE WHEN @keep AND session_id = @holder
                                                                              THEN coalesce(lease_minutes, @ttl)
                                                                              ELSE @ttl END),
                   claimed_by            = CASE WHEN @keep AND session_id = @holder THEN claimed_by
                                                ELSE @claimedBy END,
                   claimed_by_session_id = CASE WHEN @keep AND session_id = @holder THEN claimed_by_session_id
                                                ELSE @claimedBySession END,
                   lease_minutes         = CASE WHEN @keep AND session_id = @holder THEN lease_minutes
                                                ELSE @lease END,
                   session_id            = coalesce(@holder, session_id)
             WHERE item_id = @item AND released_at IS NULL
               AND (session_id = @session
                    OR session_id = @holder
                    OR claimed_by_session_id = @session
                    OR (session_id IS NULL AND agent_name = @agent AND holder_user_id IS NOT DISTINCT FROM @user));
            """;

        var extended = await connection.ExecuteAsync(new CommandDefinition(
            extend,
            Typed(new()
            {
                ["item"] = (item.Id, DbType.Guid),
                ["agent"] = (actor.Name, DbType.String),
                ["user"] = (actor.UserId, DbType.Guid),
                ["session"] = (actor.SessionId, DbType.Guid),
                ["holder"] = (holder.SessionId, DbType.Guid),
                ["keep"] = (!delegated && lease is null, DbType.Boolean),
                ["claimedBy"] = (delegated ? actor.Label : null, DbType.String),
                ["claimedBySession"] = (delegated ? actor.SessionId : null, DbType.Guid),
                ["lease"] = (lease, DbType.Int32),
                ["ttl"] = (ttl, DbType.Int32),
            }),
            transaction,
            cancellationToken: cancellationToken));

        await SessionTreeAsync(connection, transaction, holder, treeLock.Tree, cancellationToken);

        if (extended > 0)
        {
            return $"extended, held by {label}";
        }

        const string acquire =
            """
            INSERT INTO claim
                (id, item_id, serialization_key, key_workspace_id, key_user_id, tree, writes, agent_name, holder_user_id,
                 role, acquired_at, heartbeat_at, expires_at, session_id, lease_minutes, claimed_by, claimed_by_session_id)
            VALUES (@id, @item, @key, @keyWorkspace, @keyUser, @tree, true, @agent, @user,
                    @role::actor_role, now(), now(), now() + make_interval(mins => @ttl), @session,
                    @lease, @claimedBy, @claimedBySession);
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            acquire,
            Typed(new()
            {
                ["id"] = (Guid.CreateVersion7(), DbType.Guid),
                ["item"] = (item.Id, DbType.Guid),
                ["key"] = (treeLock.Key, DbType.String),
                ["keyWorkspace"] = (treeLock.Workspace, DbType.Guid),
                ["keyUser"] = (treeLock.User, DbType.Guid),
                ["tree"] = (treeLock.Tree, DbType.String),
                ["agent"] = (actor.Name, DbType.String),
                ["user"] = (actor.UserId, DbType.Guid),
                ["role"] = (Pg.Label(actor.Role), DbType.String),
                ["ttl"] = (ttl, DbType.Int32),
                ["session"] = (holder.SessionId, DbType.Guid),
                ["lease"] = (lease, DbType.Int32),
                ["claimedBy"] = (delegated ? actor.Label : null, DbType.String),
                ["claimedBySession"] = (delegated ? actor.SessionId : null, DbType.Guid),
            }),
            transaction,
            cancellationToken: cancellationToken));

        await EventAsync(
            connection,
            transaction,
            item.WorkspaceId,
            item.Id,
            actor,
            "claim_acquired",
            $"{item.Ref} claimed by {label} for {ttl}m",
            new
            {
                agent = actor.Name,
                session = holder.Session,
                claimed_by = delegated ? actor.Label : null,
                serialization_key = treeLock.Key,
                tree = treeLock.Tree,
                ttl_minutes = ttl,
            },
            cancellationToken);

        return $"acquired by {label}, {ttl}m lease on \"{Glyph.Flat(treeLock.Key)}\"";
    }

    /// <summary>Parameters whose nulls still carry a type, which a <c>CASE</c> or <c>coalesce</c> cannot infer.</summary>
    private static DynamicParameters Typed(Dictionary<string, (object? Value, DbType Type)> values)
    {
        var parameters = new DynamicParameters();
        foreach (var (name, (value, type)) in values)
        {
            parameters.Add(name, value, type);
        }

        return parameters;
    }

    /// <summary>doc 11 — the tree a session last claimed on, and its first one as its cwd if no header named one.</summary>
    private static async Task SessionTreeAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Actor actor,
        string? tree,
        CancellationToken cancellationToken)
    {
        if (actor.SessionId is not { } session || tree is null)
        {
            return;
        }

        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE agent_session SET tree = @tree, cwd = coalesce(cwd, @tree) WHERE id = @session;",
            new { session, tree },
            transaction,
            cancellationToken: cancellationToken));
    }

    /// <summary>Leaving <c>in_progress</c> by any route releases the claim — doc 07 §2.</summary>
    private static async Task<string?> ReleaseAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        ItemStatus status,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE claim SET released_at = now(), release_reason = @reason
             WHERE item_id = @item AND released_at IS NULL;
            """;

        var released = await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { item = item.Id, reason = Pg.Label(status) },
            transaction,
            cancellationToken: cancellationToken));

        return released > 0 ? "released" : null;
    }

    /// <summary>
    /// doc 07 §5 — "any write by the claim holder extends it. No separate heartbeat call: a tool
    /// that needs a keep-alive gets forgotten." Every write tool calls this on its way out. A claim taken for a
    /// session renews on its claimer's writes too, to its own lease (mcp-fit/6).
    /// </summary>
    private static async Task HeartbeatAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid itemId,
        Actor actor,
        int ttl,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            UPDATE claim c
               SET heartbeat_at = now(), expires_at = now() + make_interval(mins => coalesce(c.lease_minutes, @ttl))
              FROM item i
             WHERE i.id = @item
               AND c.released_at IS NULL
               AND (c.session_id = @session
                    OR c.claimed_by_session_id = @session
                    OR (c.session_id IS NULL AND c.agent_name = @agent AND c.holder_user_id IS NOT DISTINCT FROM @user))
               AND (c.item_id = i.id OR c.item_id = ANY(i.ancestors));
            """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql,
            new { item = itemId, agent = actor.Name, user = actor.UserId, session = actor.SessionId, ttl },
            transaction,
            cancellationToken: cancellationToken));
    }

    private static async Task<int> TtlAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT CASE WHEN settings->>'claim_ttl_minutes' ~ '^[0-9]+$'
                        THEN (settings->>'claim_ttl_minutes')::int
                        ELSE @fallback END
            FROM   workspace WHERE id = @workspace;
            """;

        var ttl = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            sql,
            new { workspace = workspaceId, fallback = DefaultTtlMinutes },
            transaction,
            cancellationToken: cancellationToken));

        return ttl is > 0 ? ttl.Value : DefaultTtlMinutes;
    }

    /// <summary>
    /// The write lock is on the working tree, not the item: the <c>tree</c> the caller named, else
    /// the run's <c>serialization_key</c>, else the project's repo root, else the ref scope. Every key
    /// locks within its workspace — a path as much as a name — so one tenant's claim never holds
    /// another's tree. A path is also a folder on one person's machine, so it locks per user: another
    /// user's same path is another checkout. A name — a branch, the ref scope — is the workspace's, and a
    /// path claimed by a credential with no user stays the workspace's too. Walking the ancestor chain server-side keeps <c>uuid[]</c> out of the Dapper
    /// mapping, which needs a handler it does not have.
    /// </summary>
    private static async Task<TreeLock> TreeLockAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ItemRow item,
        Actor actor,
        string? tree,
        CancellationToken cancellationToken)
    {
        if (WorkingTree.Parse(tree) is { } named)
        {
            return new TreeLock(named.Key, item.WorkspaceId, named.IsPath ? actor.UserId : null, named.Key);
        }

        const string sql =
            """
            SELECT
              (SELECT rc.serialization_key
                 FROM item i
                 CROSS JOIN LATERAL unnest(i.ancestors || i.id) WITH ORDINALITY AS u(id, ord)
                 JOIN run_config rc ON rc.item_id = u.id
                WHERE i.id = @item AND rc.serialization_key <> ''
                ORDER BY u.ord DESC LIMIT 1) AS "RunKey",
              (SELECT pc.repo_root
                 FROM item i
                 CROSS JOIN LATERAL unnest(i.ancestors || i.id) WITH ORDINALITY AS u(id, ord)
                 JOIN project_config pc ON pc.item_id = u.id
                WHERE i.id = @item AND pc.repo_root <> ''
                ORDER BY u.ord DESC LIMIT 1) AS "RepoRoot",
              (SELECT coalesce(s.ref, i.ref)
                 FROM item i LEFT JOIN item s ON s.id = i.ref_scope_id
                WHERE i.id = @item) AS "ScopeRef";
            """;

        var sources = await connection.QuerySingleAsync<KeySources>(new CommandDefinition(
            sql,
            new { item = item.Id },
            transaction,
            cancellationToken: cancellationToken));

        var key = sources.RunKey ?? sources.RepoRoot ?? sources.ScopeRef ?? item.Id.ToString();
        return new TreeLock(key, item.WorkspaceId, WorkingTree.Parse(key)?.IsPath == true ? actor.UserId : null, null);
    }
}
