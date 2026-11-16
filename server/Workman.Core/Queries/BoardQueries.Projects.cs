using Workman.Core.Domain;

namespace Workman.Core.Queries;

/// <summary>A run under a project, as the project brief lists it.</summary>
public sealed record ProjectRunRow(
    Guid Id,
    Guid WorkspaceId,
    string Ref,
    string Kind,
    string Title,
    string? Intent,
    ItemStatus Status,
    Guid? RefScopeId,
    Guid? ParentId,
    string? QualifiedRef,
    string? Resolution,
    DateTimeOffset? ClosedAt,
    bool Backlog,
    int TasksDone,
    int TasksTotal,
    int Unfinished,
    string? Commit)
{
    public ScopeRef Scope => new(Id, WorkspaceId, Ref, Kind, Title, Intent, Status, RefScopeId, ParentId, QualifiedRef);

    public bool Closed => Status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;
}

/// <summary>Open work waiting under a project's backlog run, or a follow-up anywhere under it.</summary>
public sealed record BacklogItemRow(string Ref, string QualifiedRef, string Kind, string Title, bool RefShared);

public sealed partial class BoardQueries
{
    /// <summary>
    /// Every run under a project, open first in <see cref="DefaultScopeAsync"/>'s busiest order, then
    /// closed newest first. <c>Backlog</c> marks the run <c>target:"backlog"</c> defers into, matched
    /// as <c>WorkWriter.BacklogRunAsync</c> matches it.
    /// </summary>
    public async Task<IReadOnlyList<ProjectRunRow>> ProjectRunsAsync(
        Guid projectId,
        string backlogTitle,
        CancellationToken cancellationToken = default)
        => [.. (await ProjectRunsAsync([projectId], backlogTitle, cancellationToken))[projectId]];

    /// <summary><see cref="ProjectRunsAsync(Guid, string, CancellationToken)"/> for many projects in one command.</summary>
    public async Task<ILookup<Guid, ProjectRunRow>> ProjectRunsAsync(
        IReadOnlyCollection<Guid> projectIds,
        string backlogTitle,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT {ScopeColumns},
                    i.resolution::text AS "Resolution",
                    i.closed_at        AS "ClosedAt",
                    (i.parent_id = sc.id AND (lower(i.ref) = 'backlog' OR i.title = @title)) AS "Backlog",
                    coalesce(t.done, 0)::int  AS "TasksDone",
                    coalesce(t.total, 0)::int AS "TasksTotal",
                    (coalesce(r.open_count,0) + coalesce(r.doing_count,0)
                     + coalesce(r.review_count,0))::int AS "Unfinished",
                    h.value AS "Commit",
                    {Scoped.Column}
             FROM   item i
             JOIN   {Scoped.Scopes} ON i.ancestors @> ARRAY[sc.id]::uuid[]
             LEFT JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             LEFT JOIN item_rollup r ON r.id = i.id
             LEFT JOIN LATERAL (
                    SELECT count(*) FILTER (WHERE c.status = 'done') AS done,
                           count(*) FILTER (WHERE c.status NOT IN ('cancelled','superseded')) AS total
                    FROM   item c
                    WHERE  c.ancestors @> ARRAY[i.id]::uuid[] AND {WorkmanQueries.Counted}) t ON true
             LEFT JOIN LATERAL (
                    SELECT a.value
                    FROM   artifact a
                    JOIN   item c ON c.id = a.item_id
                    WHERE  a.kind = 'commit'
                      AND  (c.id = i.id OR c.ancestors @> ARRAY[i.id]::uuid[])
                    ORDER  BY a.at DESC
                    LIMIT  1) h ON true
             WHERE  i.kind = 'run'
             ORDER BY (i.status IN ('done','cancelled','superseded')),
                      CASE WHEN i.status IN ('done','cancelled','superseded') THEN 0
                           ELSE coalesce(r.open_count,0) + coalesce(r.doing_count,0)
                                + coalesce(r.review_count,0) END DESC,
                      i.closed_at DESC NULLS LAST,
                      i.seq;
             """;

        return await ScopedAsync<ProjectRunRow>(
            sql, new { scopes = Scoped.Ids(projectIds), title = backlogTitle }, cancellationToken);
    }

    /// <summary>
    /// The project's waiting work: open leaves of its backlog run, and open follow-ups anywhere under
    /// the project that sit outside it.
    /// </summary>
    public async Task<IReadOnlyList<BacklogItemRow>> ProjectBacklogAsync(
        Guid projectId,
        Guid? backlogRunId,
        CancellationToken cancellationToken = default)
        => [.. (await ProjectBacklogAsync([(projectId, backlogRunId)], cancellationToken))[projectId]];

    /// <summary>
    /// <see cref="ProjectBacklogAsync(Guid, Guid?, CancellationToken)"/> for many projects, each with its backlog run,
    /// in one command.
    /// </summary>
    public async Task<ILookup<Guid, BacklogItemRow>> ProjectBacklogAsync(
        IReadOnlyCollection<(Guid Project, Guid? BacklogRun)> projects,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT i.ref AS "Ref",
                   CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END AS "QualifiedRef",
                   i.kind  AS "Kind",
                   i.title AS "Title",
                   EXISTS (SELECT 1 FROM item o
                           WHERE  o.workspace_id = i.workspace_id
                             AND  lower(o.ref) = lower(i.ref)
                             AND  o.id <> i.id) AS "RefShared",
                   sc.id AS "ForScope"
            FROM   unnest(@projects::uuid[], @backlogs::uuid[]) AS sc(id, backlog)
            JOIN   item i
                   ON ((sc.backlog IS NOT NULL AND i.ancestors @> ARRAY[sc.backlog]::uuid[]
                        AND i.kind IN ('task','followup','subtask','spike','question','incident'))
                       OR (i.kind = 'followup' AND i.ancestors @> ARRAY[sc.id]::uuid[]))
            LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
            WHERE  i.status NOT IN ('done','cancelled','superseded')
            ORDER  BY (sc.backlog IS NOT NULL AND i.ancestors @> ARRAY[sc.backlog]::uuid[]) DESC,
                      i.depth, i.seq, i.id;
            """;

        var distinct = projects.DistinctBy(p => p.Project).ToList();
        return await ScopedAsync<BacklogItemRow>(
            sql,
            new
            {
                projects = distinct.Select(p => p.Project).ToArray(),
                backlogs = distinct.Select(p => p.BacklogRun).ToArray(),
            },
            cancellationToken);
    }
}
