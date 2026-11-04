using System.Text.Json.Serialization;
using Dapper;
using Npgsql;
using Workman.Core.Domain;
using Workman.Core.Rendering;

namespace Workman.Core.Writing;

/// <summary>
/// A repository as the caller names it — <c>git remote get-url origin</c> and the parent of
/// <c>git rev-parse --path-format=absolute --git-common-dir</c>. An object rather than one string
/// because the two keys are independent: a repo with no remote still has a root.
/// </summary>
public sealed record RepoArg(
    [property: JsonPropertyName("remote")] string? Remote = null,
    [property: JsonPropertyName("root")] string? Root = null)
{
    public IReadOnlyList<RepoKey> Keys() => RepoKeys.Of(Remote, Root);
}

public sealed partial class WorkWriter
{
    private sealed record BoundKey(string Key, string ProjectRef);

    /// <summary>
    /// Binds <paramref name="keys"/> to the project, or refuses naming the project that already
    /// holds one: one project per repository. A key the project already holds is left as it is.
    /// The caller returns the refusal without committing, so its transaction rolls back whole.
    /// <para>
    /// Insert first, then ask who holds a key that did not insert: the unique index waits out a
    /// concurrent binder, so the owner read after it sees that binder's committed row.
    /// </para>
    /// <para>
    /// A root is a path on <paramref name="userId"/>'s machine, so it is bound for them alone: another user's same path
    /// is another checkout, free to bind elsewhere. A remote names one repository for everyone and is the workspace's.
    /// </para>
    /// </summary>
    private static async Task<(IReadOnlyList<RepoKey> Added, string? Refusal)> BindReposAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid projectId,
        string projectRef,
        Guid? userId,
        IReadOnlyList<RepoKey> keys,
        CancellationToken cancellationToken)
    {
        const string insert =
            """
            INSERT INTO project_repo (id, workspace_id, project_item_id, user_id, kind, key)
            VALUES (@id, @workspace, @project, @user, @kind, @key)
            ON CONFLICT (workspace_id, user_id, key) DO NOTHING;
            """;

        const string owner =
            """
            SELECT r.key AS "Key", p.ref AS "ProjectRef"
            FROM   project_repo r JOIN item p ON p.id = r.project_item_id
            WHERE  r.workspace_id = @workspace AND r.user_id IS NOT DISTINCT FROM @user AND r.key = @key
              AND  r.project_item_id <> @project;
            """;

        var added = new List<RepoKey>();
        foreach (var key in keys)
        {
            var user = RepoKeys.UserOf(key, userId);
            var rows = await connection.ExecuteAsync(new CommandDefinition(
                insert,
                new { id = Guid.CreateVersion7(), workspace = workspaceId, project = projectId, user, kind = key.Kind, key = key.Key },
                transaction,
                cancellationToken: cancellationToken));

            if (rows > 0)
            {
                added.Add(key);
                continue;
            }

            var held = await connection.QueryFirstOrDefaultAsync<BoundKey>(new CommandDefinition(
                owner,
                new { workspace = workspaceId, user, key = key.Key, project = projectId },
                transaction,
                cancellationToken: cancellationToken));

            if (held is not null)
            {
                return ([], Refusal.Blocked(
                    $"{key.Kind} \"{key.Key}\" is already bound to project {held.ProjectRef}.",
                    $"this repo is project {held.ProjectRef} — one project per repository, so "
                    + $"{projectRef} cannot hold it too",
                    $"work_brief(scope:\"{held.ProjectRef}\") to work in it",
                    $"work_update(ref:\"{projectRef}\", repo:{RepoKeys.Literal(keys)}, take:true) to move the repo "
                    + $"from {held.ProjectRef} to {projectRef}",
                    $"work_brief(repo:{RepoKeys.Literal(keys)}) — the same project, found from the repo"));
            }

            var own = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                """
                SELECT EXISTS (SELECT 1 FROM project_repo
                               WHERE  workspace_id = @workspace AND user_id IS NOT DISTINCT FROM @user AND key = @key
                                 AND  project_item_id = @project);
                """,
                new { workspace = workspaceId, user, key = key.Key, project = projectId },
                transaction,
                cancellationToken: cancellationToken));

            if (own)
            {
                continue;
            }

            // Held, but by a project the caller is not a member of (doc 02 §15b): the key is the workspace's to give
            // once, so the refusal says it is taken and names nothing of the project that took it.
            return ([], Refusal.Blocked(
                $"{key.Kind} \"{key.Key}\" is already bound to a project of this workspace that you are not a member of.",
                $"one project per repository, so {projectRef} cannot hold it too; that project's members can add you",
                $"work_brief(repo:{RepoKeys.Literal(keys)}) once a member has added you — it finds that project",
                $"the same call without repo: to leave {projectRef} unbound"));
        }

        return (added, null);
    }

    private sealed record HeldRow(Guid ProjectId, string ProjectRef, Guid? UserId, string Kind, string Key);

    /// <summary>
    /// <c>take:true</c>: every row another project holds for one of <paramref name="keys"/> moves to this project, the
    /// user it was bound for kept, so another user's root opens the new project too. Only rows the caller can see move:
    /// a project they are not a member of keeps its rows, and the bind that follows refuses as it would without take.
    /// Each source project journals what left it; the caller journals the arrival.
    /// </summary>
    private static async Task<IReadOnlyList<HeldRow>> TakeReposAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid projectId,
        string projectRef,
        Actor actor,
        IReadOnlyList<RepoKey> keys,
        CancellationToken cancellationToken)
    {
        const string take =
            """
            WITH gone AS (
                DELETE FROM project_repo r
                WHERE  r.workspace_id = @workspace AND r.project_item_id <> @project
                  AND  (r.kind, r.key) IN (SELECT * FROM unnest(@kinds::text[], @keys::text[]))
                RETURNING r.project_item_id, r.user_id, r.kind, r.key)
            SELECT g.project_item_id AS "ProjectId", p.ref AS "ProjectRef", g.user_id AS "UserId", g.kind AS "Kind", g.key AS "Key"
            FROM   gone g JOIN item p ON p.id = g.project_item_id
            ORDER  BY p.ref, g.kind, g.key;
            """;

        const string insert =
            """
            INSERT INTO project_repo (id, workspace_id, project_item_id, user_id, kind, key)
            VALUES (@id, @workspace, @project, @user, @kind, @key)
            ON CONFLICT (workspace_id, user_id, key) DO NOTHING;
            """;

        var moved = (await connection.QueryAsync<HeldRow>(new CommandDefinition(
            take,
            new { workspace = workspaceId, project = projectId, kinds = keys.Select(k => k.Kind).ToArray(), keys = keys.Select(k => k.Key).ToArray() },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        foreach (var row in moved)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                insert,
                new { id = Guid.CreateVersion7(), workspace = workspaceId, project = projectId, user = row.UserId, kind = row.Kind, key = row.Key },
                transaction,
                cancellationToken: cancellationToken));
        }

        foreach (var source in moved.GroupBy(r => (r.ProjectId, r.ProjectRef)))
        {
            var removed = source.Select(r => $"{r.Kind} {r.Key}").Distinct().ToArray();
            await EventAsync(
                connection, transaction, workspaceId, source.Key.ProjectId, actor, "repo",
                $"{source.Key.ProjectRef}: {string.Join(", ", removed)} moved to {projectRef} by {actor.Label}",
                new { moved_to = projectRef, keys = removed, rows = source.Count() },
                cancellationToken);

            await EventAsync(
                connection, transaction, workspaceId, projectId, actor, "repo",
                $"{projectRef}: {string.Join(", ", removed)} taken from {source.Key.ProjectRef} by {actor.Label}",
                new { taken_from = source.Key.ProjectRef, keys = removed, rows = source.Count() },
                cancellationToken);
        }

        return moved;
    }

    /// <summary>
    /// <c>unbind:</c> removes <paramref name="keys"/> from the project: the remote, and the root for every user it is
    /// bound for here — the same path bound to the same project is the same repository. A project left with no key is
    /// an unbound project, still opened by its ref.
    /// </summary>
    private static async Task<IReadOnlyList<RepoKey>> UnbindReposAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid workspaceId,
        Guid projectId,
        string projectRef,
        Actor actor,
        IReadOnlyList<RepoKey> keys,
        CancellationToken cancellationToken)
    {
        const string sql =
            """
            DELETE FROM project_repo r
            WHERE  r.workspace_id = @workspace AND r.project_item_id = @project
              AND  (r.kind, r.key) IN (SELECT * FROM unnest(@kinds::text[], @keys::text[]))
            RETURNING r.kind AS "Kind", r.key AS "Key";
            """;

        var rows = (await connection.QueryAsync<RepoKey>(new CommandDefinition(
            sql,
            new { workspace = workspaceId, project = projectId, kinds = keys.Select(k => k.Kind).ToArray(), keys = keys.Select(k => k.Key).ToArray() },
            transaction,
            cancellationToken: cancellationToken))).ToList();

        var removed = keys.Where(k => rows.Contains(k)).ToList();
        if (removed.Count > 0)
        {
            var named = removed.Select(k => $"{k.Kind} {k.Key}").ToArray();
            await EventAsync(
                connection, transaction, workspaceId, projectId, actor, "repo",
                $"{projectRef}: {string.Join(", ", named)} unbound by {actor.Label}",
                new { unbound = named, rows = rows.Count },
                cancellationToken);
        }

        return removed;
    }

    private static string NotAProject(string call, string reference, string kind)
        => Refusal.Blocked(
            $"{call} — repo: binds a project, and {reference} is a {kind}.",
            "a repository belongs to its project; runs and tasks inside it find it from there",
            "work_update(ref:\"<the project's ref>\", repo:{remote:\"…\", root:\"…\"})",
            "work_brief(repo:{remote:\"…\", root:\"…\"}) to find the project this repo is bound to");

    private static string RepoLine(IReadOnlyList<RepoKey> keys)
        => "repo      " + string.Join(", ", keys.Select(k => $"{k.Kind} {k.Key}"));
}
