using System.Data;
using Dapper;
using Npgsql;
using Workman.Core.Data;

namespace Workman.Core.Queries;

/// <summary>
/// The reads <see cref="WorkmanQueries"/> does not have. U1 answered "what is actionable" and
/// "what is unclosed"; a brief additionally needs the scope's identity, the constraints that bind
/// it, the notes addressed to it, the evidence behind each row and the six things the attention
/// block ranks. Everything here is a projection — no statement in this file writes.
/// </summary>
public sealed partial class BoardQueries(NpgsqlDataSource dataSource)
{
    private readonly WorkmanConnections _connections = new(dataSource);

    /// <summary>Self or anything beneath it. The subtree predicate every query below repeats.</summary>
    private const string InScope = "(i.id = @scope OR i.ancestors @> ARRAY[@scope]::uuid[])";

    public async Task<Guid?> WorkspaceAsync(string? slug, CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT id FROM workspace
            WHERE @slug IS NULL OR slug = @slug
            ORDER BY created_at, slug
            LIMIT 1;
            """;

        await using var connection = await _connections.OwnerBypassAsync(Bypass.WorkspaceBinding, cancellationToken);
        return await connection.QuerySingleOrDefaultAsync<Guid?>(
            new CommandDefinition(sql, new { slug }, cancellationToken: cancellationToken));
    }

    /// <summary>
    /// The scope an unqualified call lands on: the run with the most unfinished work, else any run,
    /// else the project. A cold agent that names nothing still gets the board it meant.
    /// </summary>
    public Task<ScopeRef?> DefaultScopeAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
        => DefaultScopeAsync(workspaceId, null, cancellationToken);

    /// <summary>The same order, kept to <paramref name="under"/> and its subtree when given.</summary>
    public async Task<ScopeRef?> DefaultScopeAsync(
        Guid workspaceId,
        Guid? under,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT {ScopeColumns}
             FROM   item i
             LEFT JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             JOIN   item_kind k ON k.key = i.kind
             {WorkmanQueries.Rollup}
             WHERE  i.workspace_id = @workspace AND i.kind IN ('run','project')
               AND  (@under::uuid IS NULL OR i.id = @under OR i.ancestors @> ARRAY[@under]::uuid[])
             ORDER BY EXISTS (SELECT 1 FROM item ap
                              WHERE  (ap.id = i.id OR ap.id = ANY(i.ancestors))
                                AND  ap.archived_at IS NOT NULL),
                      (i.kind = 'run') DESC,
                      (coalesce(r.open_count,0) + coalesce(r.doing_count,0)
                       + coalesce(r.review_count,0)) DESC,
                      k.rank, i.seq
             LIMIT 1;
             """;

        return await SingleAsync<ScopeRef>(sql, new { workspace = workspaceId, under }, cancellationToken);
    }

    /// <summary>
    /// Every project in the workspace, archived ones included (the caller decides what to hide),
    /// newest activity first: the newest journal event anywhere in the project's subtree. Ref breaks
    /// ties so the order is stable between polls.
    /// </summary>
    public async Task<IReadOnlyList<ProjectRow>> ProjectsAsync(
        Guid workspaceId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT p.id          AS "Id",
                   p.ref         AS "Ref",
                   p.title       AS "Title",
                   p.status      AS "Status",
                   coalesce(r.runs, 0)::int AS "RunCount",
                   p.archived_at AS "ArchivedAt",
                   a.at          AS "LastActivityAt",
                   coalesce(r.open_runs, 0)::int AS "OpenRuns"
            FROM   item p
            LEFT   JOIN LATERAL (
                     SELECT count(*) AS runs,
                            count(*) FILTER (WHERE c.status NOT IN ('done','cancelled','superseded')) AS open_runs
                     FROM   item c
                     WHERE  c.kind = 'run' AND c.ancestors @> ARRAY[p.id]::uuid[]) r ON true
            LEFT   JOIN LATERAL (
                     SELECT max(e.at) AS at
                     FROM   event e JOIN item x ON x.id = e.item_id
                     WHERE  x.id = p.id OR x.ancestors @> ARRAY[p.id]::uuid[]) a ON true
            WHERE  p.workspace_id = @workspace AND p.kind = 'project'
            ORDER  BY a.at DESC NULLS LAST, p.ref;
            """;

        return await ListAsync<ProjectRow>(sql, new { workspace = workspaceId }, cancellationToken);
    }

    /// <summary>
    /// Resolves a ref. <c>2.3</c> looks anywhere in the workspace; <c>entity-store/2.3</c> looks
    /// inside that run only. A run (or project) numbers its own refs, so it is qualified by its
    /// parent — <c>autowork/backlog</c> — or by itself, <c>backlog/backlog</c>. A project may lead:
    /// <c>autowork/m5/13</c> is the run form inside that project, and <c>workman/repo-map-stale</c>
    /// is anything under the project, tried only when no run qualifies the two-part form, so every
    /// form that resolved before still resolves to the same item. More than one row back means the
    /// ref is ambiguous, which the caller reports with <see cref="UniqueRefsAsync"/> rather than
    /// guessing. A ref never contains '/' (work_create refuses one), so each '/' is a qualifier.
    /// </summary>
    public async Task<IReadOnlyList<ScopeRef>> ResolveAsync(
        Guid workspaceId,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var parts = reference.Split('/');
        if (parts.Length > 3 || (parts.Length > 1 && parts.Any(p => p.Length == 0)))
        {
            return [];
        }

        return parts switch
        {
            [var leaf] => await ResolveAsync(workspaceId, null, null, leaf, cancellationToken),
            [var project, var qualifier, var leaf] =>
                NumberedInside(await ResolveAsync(workspaceId, project, qualifier, leaf, cancellationToken)),
            [var qualifier, var leaf] =>
                await ResolveAsync(workspaceId, null, qualifier, leaf, cancellationToken) is { Count: > 0 } byRun
                    ? TheProjectItself(qualifier, leaf, byRun)
                    : await ResolveAsync(workspaceId, qualifier, null, leaf, cancellationToken),
            _ => [],
        };
    }

    /// <summary>
    /// mcp-fit/1. <c>project/scope/ref</c> names the ref numbered inside that scope. A run matched only by
    /// its own name or its parent's yields to it, or a followup under project <c>acme</c> beside a run
    /// <c>acme/ga4</c> had no form at all: <c>acme/acme/ga4</c> named both. Only strings that named
    /// both before change their answer.
    /// </summary>
    private static IReadOnlyList<ScopeRef> NumberedInside(IReadOnlyList<ScopeRef> matches)
        => matches.Any(m => m.RefScopeId != m.Id) && matches.Any(m => m.RefScopeId == m.Id)
            ? [.. matches.Where(m => m.RefScopeId != m.Id)]
            : matches;

    /// <summary>
    /// mcp-fit/1. <c>neo/neo</c> is a project's only qualified form, and a run <c>neo</c> anywhere
    /// matched it too, leaving the project unreachable. A project is a root and unique by name, so
    /// <c>x/x</c> names it; the run keeps <c>parent/x</c> and <c>project/parent/x</c>.
    /// </summary>
    private static IReadOnlyList<ScopeRef> TheProjectItself(string qualifier, string leaf, IReadOnlyList<ScopeRef> matches)
        => matches.Count > 1
           && qualifier.Equals(leaf, StringComparison.OrdinalIgnoreCase)
           && matches.Where(m => m.Kind == "project").ToList() is [var project]
            ? [project]
            : matches;

    private async Task<IReadOnlyList<ScopeRef>> ResolveAsync(
        Guid workspaceId,
        string? project,
        string? qualifier,
        string leaf,
        CancellationToken cancellationToken)
    {
        const string sql =
            $"""
             SELECT {ScopeColumns}
             FROM   item i
             LEFT JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             LEFT JOIN item p ON p.id = i.parent_id
             JOIN   item_kind k ON k.key = i.kind
             WHERE  i.workspace_id = @workspace
               AND  lower(i.ref) = lower(@leaf)
               AND  (@qualifier IS NULL
                     OR lower(coalesce(s.ref, i.ref)) = lower(@qualifier)
                     OR (i.ref_scope_id = i.id AND lower(p.ref) = lower(@qualifier)))
               AND  (@project IS NULL
                     OR EXISTS (SELECT 1 FROM item pj
                                WHERE  pj.id = ANY(i.ancestors)
                                  AND  pj.kind = 'project'
                                  AND  lower(pj.ref) = lower(@project)))
             ORDER BY k.rank, i.seq;
             """;

        return await ListAsync<ScopeRef>(
            sql,
            new { workspace = workspaceId, leaf, qualifier, project },
            cancellationToken);
    }

    /// <summary>
    /// ws/13. For each match of an ambiguous ref, a string that resolves to that item and nothing
    /// else — checked by resolving it, because a run's <c>QualifiedRef</c> is its bare ref and was
    /// printed back as the very string that had just been refused. A match no form can single out is
    /// left out; the caller routes to <c>work_tree</c> for it.
    /// </summary>
    public async Task<IReadOnlyList<string>> UniqueRefsAsync(
        Guid workspaceId,
        IEnumerable<ScopeRef> matches,
        CancellationToken cancellationToken = default)
    {
        var forms = new List<string>();
        foreach (var match in matches)
        {
            if (await UniqueRefAsync(workspaceId, match, cancellationToken) is { } form)
            {
                forms.Add(form);
            }
        }

        return forms;
    }

    /// <summary>The first of the bare ref, the run-qualified ref, for a run its parent- and
    /// self-qualified forms, then the project-qualified <c>project/ref</c> and
    /// <c>project/run/ref</c>, that resolves to <paramref name="match"/> alone — shortest first.</summary>
    public async Task<string?> UniqueRefAsync(
        Guid workspaceId,
        ScopeRef match,
        CancellationToken cancellationToken = default)
    {
        var forms = new List<string> { match.Ref };

        var qualified = match.QualifiedRef is { Length: > 0 } q ? q : match.Ref;
        forms.Add(qualified);

        string? parentRef = null;
        if (match.RefScopeId == match.Id)
        {
            if (match.ParentId is { } parentId
                && await SingleAsync<string>("SELECT ref FROM item WHERE id = @id;", new { id = parentId }, cancellationToken)
                    is { Length: > 0 } parent)
            {
                parentRef = parent;
                forms.Add($"{parent}/{match.Ref}");
            }

            forms.Add($"{match.Ref}/{match.Ref}");
        }

        if (await ProjectRefAsync(match.Id, cancellationToken) is { Length: > 0 } project)
        {
            forms.Add($"{project}/{match.Ref}");
            forms.Add($"{project}/{qualified}");

            if (parentRef is not null)
            {
                forms.Add($"{project}/{parentRef}/{match.Ref}");
            }
        }

        foreach (var form in forms.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (await ResolveAsync(workspaceId, form, cancellationToken) is [var only] && only.Id == match.Id)
            {
                return form;
            }
        }

        return null;
    }

    /// <summary>The nearest project above the item, or null for a project or an orphan.</summary>
    private Task<string?> ProjectRefAsync(Guid itemId, CancellationToken cancellationToken)
    {
        const string sql =
            """
            SELECT pj.ref
            FROM   item i
            JOIN   item pj ON pj.id = ANY(i.ancestors) AND pj.kind = 'project'
            WHERE  i.id = @id
            ORDER  BY array_position(i.ancestors, pj.id) DESC
            LIMIT  1;
            """;

        return SingleAsync<string>(sql, new { id = itemId }, cancellationToken);
    }

    /// <summary>
    /// The ref to print where it will be called or read back (ws/10, ws/13, doc 10 rule 2): bare
    /// while no other item shares it, else run-qualified while that resolves to it alone, else the
    /// form <see cref="UniqueRefAsync"/> checked to — which for a run or project, numbering its own
    /// refs, is never its bare ref, and for an item in two same-named runs is project-qualified.
    /// </summary>
    public async Task<string> CallableRefAsync(
        ScopeRef item,
        bool refShared,
        CancellationToken cancellationToken = default)
    {
        if (!refShared)
        {
            return item.Ref;
        }

        var qualified = item.QualifiedRef ?? item.Ref;
        if (item.RefScopeId != item.Id
            && await ResolveAsync(item.WorkspaceId, qualified, cancellationToken) is [var only]
            && only.Id == item.Id)
        {
            return qualified;
        }

        return await UniqueRefAsync(item.WorkspaceId, item, cancellationToken) ?? qualified;
    }

    /// <summary>
    /// <see cref="UniqueRefAsync"/> for a row that carries only an id and its bare ref, such as a
    /// search hit or a row <c>work_create</c> just committed: the bare ref while it resolves to that
    /// item alone, else the shortest form that does.
    /// </summary>
    public async Task<string> PrintableRefAsync(
        Guid workspaceId,
        Guid itemId,
        string reference,
        CancellationToken cancellationToken = default)
    {
        var matches = await ResolveAsync(workspaceId, reference, cancellationToken);
        if (matches is [var only] && only.Id == itemId)
        {
            return reference;
        }

        return matches.FirstOrDefault(m => m.Id == itemId) is { } match
            ? await UniqueRefAsync(workspaceId, match, cancellationToken) ?? match.QualifiedRef ?? reference
            : reference;
    }

    /// <summary>
    /// mcp-fit/1. The ref to print for an item outside the reader's run: run-qualified while that resolves to it
    /// alone, else <see cref="UniqueRefAsync"/>'s form — never bare just because the bare ref happens to be unique.
    /// </summary>
    public async Task<string> RunQualifiedRefAsync(
        Guid workspaceId,
        Guid itemId,
        string reference,
        CancellationToken cancellationToken = default)
        => (await ResolveAsync(workspaceId, reference, cancellationToken)).FirstOrDefault(m => m.Id == itemId) is { } match
            ? await CallableRefAsync(match, refShared: true, cancellationToken)
            : reference;

    /// <summary>What must be true to accept a phase (doc 02, <c>item.gate_criterion</c>); null when none was recorded.</summary>
    public Task<string?> GateCriterionAsync(Guid itemId, CancellationToken cancellationToken = default)
        => SingleAsync<string>("SELECT gate_criterion FROM item WHERE id = @id;", new { id = itemId }, cancellationToken);

    /// <summary>An item's bare ref; null when it does not exist.</summary>
    public Task<string?> RefOfAsync(Guid itemId, CancellationToken cancellationToken = default)
        => SingleAsync<string>("SELECT ref FROM item WHERE id = @id;", new { id = itemId }, cancellationToken);

    /// <summary>The run the item is, or sits in; null for a project, a feature or an orphan.</summary>
    public async Task<ScopeRef?> RunOfAsync(Guid itemId, CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT {ScopeColumns}
             FROM   item x
             JOIN   item i ON (i.id = x.id OR i.id = ANY(x.ancestors)) AND i.kind = 'run'
             LEFT JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             WHERE  x.id = @id
             ORDER  BY coalesce(array_position(x.ancestors, i.id), 2147483647) DESC
             LIMIT  1;
             """;

        return await SingleAsync<ScopeRef>(sql, new { id = itemId }, cancellationToken);
    }

    /// <summary>
    /// The scope's own identity plus the run and project it sits under. Both ancestors are looked up
    /// by <em>kind</em> rather than taken from the parent row: a task's parent is a phase, and
    /// printing that phase as the project is how <c>work_brief</c> came to render
    /// <c>RUN 3.1 · project phase-3</c> over a task (ACCEPTANCE A-3).
    /// </summary>
    public async Task<ScopeHeader?> HeaderAsync(Guid scopeItemId, CancellationToken cancellationToken = default)
    {
        var sql =
            $"""
             SELECT i.kind      AS "Kind",
                    i.ref       AS "Ref",
                    r.ref       AS "RunRef",
                    p.ref       AS "ProjectRef",
                    rc.branch   AS "Branch",
                    h.value     AS "Head",
                    coalesce(i.intent, r.intent, p.intent) AS "Goal"
             FROM   item i
             LEFT   JOIN LATERAL ({Nearest("'run'")}) r ON true
             LEFT   JOIN run_config rc ON rc.item_id = r.id
             LEFT   JOIN LATERAL ({Nearest("'project'", "OR a.id = rc.project_item_id")}) p ON true
             LEFT   JOIN LATERAL (
                      SELECT a.value
                      FROM   artifact a
                      JOIN   item c ON c.id = a.item_id
                      WHERE  a.kind = 'commit'
                        AND  (c.id = i.id OR c.ancestors @> ARRAY[i.id]::uuid[])
                      ORDER  BY a.at DESC
                      LIMIT  1) h ON true
             WHERE  i.id = @scope;
             """;

        return await SingleAsync<ScopeHeader>(sql, new { scope = scopeItemId }, cancellationToken);
    }

    /// <summary>
    /// The nearest ancestor-or-self of one kind. <c>ancestors</c> is ordered root..parent, so the
    /// deepest match is the last position in it, and the item itself outranks any of them.
    /// </summary>
    private static string Nearest(string kind, string alsoWhere = "")
        => $"""
            SELECT a.id, a.ref, a.intent
            FROM   item a
            WHERE  a.kind = {kind}
              AND  (a.id = i.id OR a.id = ANY(i.ancestors) {alsoWhere})
            ORDER  BY (a.id = i.id) DESC,
                      (array_position(i.ancestors, a.id) IS NOT NULL) DESC,
                      array_position(i.ancestors, a.id) DESC
            LIMIT  1
            """;

    /// <summary>
    /// doc 02 §8 — the workspace rules plus every rule scoped to this item or one of its ancestors.
    /// A brief that misses a rule added after it was written is the failure this replaces.
    /// </summary>
    public async Task<IReadOnlyList<ConstraintRow>> ConstraintsAsync(
        Guid workspaceId,
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT c.ref      AS "Ref",
                   c.kind     AS "Kind",
                   c.text     AS "Text",
                   c.route    AS "Route",
                   c.severity AS "Severity"
            FROM   constraint_rule c
            CROSS  JOIN (SELECT id, ancestors FROM item WHERE id = @scope) x
            WHERE  c.workspace_id = @workspace
              AND  c.active
              AND  (c.scope_item_id IS NULL
                    OR c.scope_item_id = x.id
                    OR c.scope_item_id = ANY(x.ancestors))
            ORDER BY (c.kind = 'red_line') DESC, (c.severity = 'binding') DESC, c.created_at;
            """;

        return await ListAsync<ConstraintRow>(
            sql,
            new { workspace = workspaceId, scope = scopeItemId },
            cancellationToken);
    }

    /// <summary>
    /// The active rules a review of this scope reads: <see cref="ConstraintsAsync"/>'s, plus every
    /// rule scoped to an item beneath it.
    /// </summary>
    public async Task<int> RulesInScopeCountAsync(
        Guid workspaceId,
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
        => (await RulesInScopeCountAsync(workspaceId, [scopeItemId], cancellationToken)).GetValueOrDefault(scopeItemId);

    /// <summary>
    /// <see cref="RulesInScopeCountAsync(Guid, Guid, CancellationToken)"/> for many scopes in one command; a scope
    /// no rule reads is absent.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, int>> RulesInScopeCountAsync(
        Guid workspaceId,
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT x.id AS "Scope", count(*)::int AS "Count"
            FROM   constraint_rule c
            CROSS  JOIN (SELECT id, ancestors FROM item WHERE id = ANY(@scopes)) x
            LEFT   JOIN item s ON s.id = c.scope_item_id
            WHERE  c.workspace_id = @workspace
              AND  c.active
              AND  (c.scope_item_id IS NULL
                    OR c.scope_item_id = x.id
                    OR c.scope_item_id = ANY(x.ancestors)
                    OR s.ancestors @> ARRAY[x.id]::uuid[])
            GROUP  BY x.id;
            """;

        return (await ListAsync<ScopeCount>(
                sql, new { workspace = workspaceId, scopes = Scoped.Ids(scopeItemIds) }, cancellationToken))
            .ToDictionary(c => c.Scope, c => c.Count);
    }

    public async Task<IReadOnlyList<AssumptionRow>> AssumptionsAsync(
        Guid workspaceId,
        Guid? scopeItemId,
        bool openOnly,
        CancellationToken cancellationToken = default)
    {
        if (scopeItemId is { } scope)
        {
            return [.. (await AssumptionsAsync(workspaceId, [scope], openOnly, cancellationToken))[scope]];
        }

        const string sql =
            $"""
             SELECT {AssumptionColumns}
             FROM   assumption a
             {AssumptionJoins}
             ORDER BY a.ref;
             """;

        return await ListAsync<AssumptionRow>(sql, new { workspace = workspaceId, openOnly }, cancellationToken);
    }

    /// <summary>
    /// <see cref="AssumptionsAsync(Guid, Guid?, bool, CancellationToken)"/> for many scopes in one command: an
    /// assumption scoped to the scope or to anything beneath it.
    /// </summary>
    public async Task<ILookup<Guid, AssumptionRow>> AssumptionsAsync(
        Guid workspaceId,
        IReadOnlyCollection<Guid> scopeItemIds,
        bool openOnly,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT {AssumptionColumns},
                    {Scoped.Column}
             FROM   assumption a
             JOIN   {Scoped.Scopes}
                    ON (a.scope_item_id = sc.id
                        OR EXISTS (SELECT 1 FROM item i
                                   WHERE i.id = a.scope_item_id
                                     AND i.ancestors @> ARRAY[sc.id]::uuid[]))
             {AssumptionJoins}
             ORDER BY a.ref;
             """;

        return await ScopedAsync<AssumptionRow>(
            sql,
            new { workspace = workspaceId, scopes = Scoped.Ids(scopeItemIds), openOnly },
            cancellationToken);
    }

    private const string AssumptionColumns =
        $$"""
        a.ref            AS "Ref",
               CASE WHEN s.ref IS NULL THEN a.ref ELSE s.ref || '/' || a.ref END
                                AS "QualifiedRef",
               a.statement      AS "Statement",
               a.evidence       AS "Evidence",
               a.evidence_note  AS "EvidenceNote",
               a.reversal       AS "Reversal",
               a.state          AS "State",
               r.ref            AS "RevisitRef",
               r.status         AS "RevisitStatus",
               r.closed_at      AS "RevisitClosedAt",
               a.scope_item_id  AS "ScopeItemId",
               {{CallableAssumptionSql}}
                                AS "Callable",
               a.revisit_at     AS "RevisitAt"
        """;

    private const string AssumptionJoins =
        """
        LEFT   JOIN item s ON s.id = a.ref_scope_id
        LEFT   JOIN item r ON r.id = a.revisit_item_id
        WHERE  a.workspace_id = @workspace
          AND  (NOT @openOnly OR a.state = 'open')
        """;

    /// <summary>
    /// FINDINGS-live F-L3: every imported note of a run shares one <c>created_at</c>, so ordering on
    /// it alone left the whole block tied and the first line arbitrary. The kind is the ranking a
    /// reader can act on — a correction retracts something they would otherwise believe — and the id
    /// makes what is left deterministic rather than whatever the scan returned.
    /// <para>
    /// F-08: <paramref name="openAddresseesOnly"/> drops notes addressed to a closed item. Only the
    /// addressee's worker brief consumes a note and nobody briefs a closed item, so for attention
    /// such a note would be an obligation nobody can discharge. It stays readable everywhere else.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<NoteRow>> UnconsumedNotesAsync(
        Guid workspaceId,
        Guid? scopeItemId,
        CancellationToken cancellationToken = default,
        bool openAddresseesOnly = false)
    {
        if (scopeItemId is { } scope)
        {
            return [.. (await UnconsumedNotesAsync(workspaceId, [scope], cancellationToken, openAddresseesOnly))[scope]];
        }

        const string sql =
            $"""
             SELECT {NoteColumns}
             FROM   note n
             {NoteJoins}
             {NoteOrder};
             """;

        return await ListAsync<NoteRow>(
            sql,
            new { workspace = workspaceId, openOnly = openAddresseesOnly },
            cancellationToken);
    }

    /// <summary>
    /// <see cref="UnconsumedNotesAsync(Guid, Guid?, CancellationToken, bool)"/> for many scopes in one command: a
    /// note to or from the scope or anything beneath it.
    /// </summary>
    public async Task<ILookup<Guid, NoteRow>> UnconsumedNotesAsync(
        Guid workspaceId,
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default,
        bool openAddresseesOnly = false)
    {
        const string sql =
            $"""
             SELECT {NoteColumns},
                    {Scoped.Column}
             FROM   note n
             JOIN   {Scoped.Scopes}
                    ON (n.to_item_id = sc.id
                        OR n.from_item_id = sc.id
                        OR EXISTS (SELECT 1 FROM item i
                                   WHERE i.id IN (n.to_item_id, n.from_item_id)
                                     AND i.ancestors @> ARRAY[sc.id]::uuid[]))
             {NoteJoins}
             {NoteOrder};
             """;

        return await ScopedAsync<NoteRow>(
            sql,
            new { workspace = workspaceId, scopes = Scoped.Ids(scopeItemIds), openOnly = openAddresseesOnly },
            cancellationToken);
    }

    private const string NoteColumns =
        """
        n.kind  AS "Kind",
               n.body  AS "Body",
               t.ref   AS "ToRef",
               CASE WHEN ts.ref IS NULL THEN t.ref ELSE ts.ref || '/' || t.ref END
                      AS "ToQualifiedRef",
               f.ref   AS "FromRef",
               n.id    AS "Id",
               n.to_item_id AS "ToItemId",
               n.created_at AS "CreatedAt",
               n.acknowledged_at AS "AcknowledgedAt"
        """;

    private const string NoteJoins =
        """
        LEFT   JOIN item t  ON t.id  = n.to_item_id
        LEFT   JOIN item ts ON ts.id = t.ref_scope_id AND ts.id <> t.id
        LEFT   JOIN item f  ON f.id  = n.from_item_id
        WHERE  n.workspace_id = @workspace
          AND  n.consumed_at IS NULL
          AND  (NOT @openOnly
                OR n.to_item_id IS NULL
                OR t.status NOT IN ('done','cancelled','superseded'))
        """;

    private const string NoteOrder =
        """
        ORDER BY (n.to_item_id IS NOT NULL) DESC,
                 CASE n.kind
                   WHEN 'correction'      THEN 0
                   WHEN 'incident'        THEN 1
                   WHEN 'carry_forward'   THEN 2
                   WHEN 'do_not_rederive' THEN 3
                   WHEN 'decision'        THEN 4
                   WHEN 'environment'     THEN 5
                   ELSE 6 END,
                 n.created_at DESC,
                 n.id DESC
        """;

    public async Task<VerificationRow?> LastVerificationAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.id          AS "ItemId",
                    i.ref         AS "ItemRef",
                    v.command     AS "Command",
                    v.result_line AS "ResultLine",
                    v.passed      AS "Passed",
                    v.run_by      AS "RunBy",
                    v.at          AS "At",
                    v.confirmed_by AS "ConfirmedBy",
                    v.quote       AS "Quote"
             FROM   verification v
             JOIN   item i ON i.id = v.item_id
             WHERE  {InScope}
             ORDER  BY v.at DESC
             LIMIT  1;
             """;

        return await SingleAsync<VerificationRow>(sql, new { scope = scopeItemId }, cancellationToken);
    }

    public async Task<IReadOnlyList<VerificationRow>> VerificationsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT i.id          AS "ItemId",
                   i.ref         AS "ItemRef",
                   v.command     AS "Command",
                   v.result_line AS "ResultLine",
                   v.passed      AS "Passed",
                   v.run_by      AS "RunBy",
                   v.at          AS "At",
                   v.confirmed_by AS "ConfirmedBy",
                   v.quote       AS "Quote"
            FROM   verification v
            JOIN   item i ON i.id = v.item_id
            WHERE  v.item_id = ANY(@ids)
            ORDER  BY v.at DESC;
            """;

        return await ListAsync<VerificationRow>(sql, new { ids = itemIds.ToArray() }, cancellationToken);
    }

    /// <summary>Every work_report against the items, oldest attempt first — work_get prints them whole.</summary>
    public async Task<IReadOnlyList<ItemReportRow>> ReportsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT a.item_id    AS "ItemId",
                   a.n          AS "N",
                   r.status     AS "Status",
                   a.agent_name AS "AgentName",
                   r.raw        AS "Raw"
            FROM   report r
            JOIN   attempt a ON a.id = r.attempt_id
            WHERE  a.item_id = ANY(@ids)
            ORDER  BY a.n, r.id;
            """;

        return await ListAsync<ItemReportRow>(sql, new { ids = itemIds.ToArray() }, cancellationToken);
    }

    /// <summary>Everything the flag vocabulary of doc 06 §2 needs, one row per item.</summary>
    public async Task<IReadOnlyList<ItemFacts>> FactsAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
            SELECT i.id                         AS "Id",
                   i.ref                        AS "Ref",
                   CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                                                AS "QualifiedRef",
                   EXISTS (SELECT 1 FROM item o
                           WHERE  o.workspace_id = i.workspace_id
                             AND  lower(o.ref) = lower(i.ref)
                             AND  o.id <> i.id) AS "RefShared",
                   coalesce(c.total, 0)::int    AS "CriteriaTotal",
                   coalesce(c.ok, 0)::int       AS "CriteriaChecked",
                   coalesce(v.passed, 0)::int   AS "VerificationsPassed",
                   coalesce(v.orch, 0)::int     AS "OrchestratorVerifications",
                   a.value                      AS "Commit",
                   coalesce(r.open_count, 0) + coalesce(r.doing_count, 0)
                     + coalesce(r.blocked_count, 0)
                     + coalesce(r.review_count, 0)                         AS "OpenChildren",
                   i.review_required            AS "ReviewRequired",
                   k.requires_verify            AS "RequiresVerify",
                   i.severity                   AS "Severity",
                   p.ref                        AS "ParentRef",
                   i.started_at                 AS "StartedAt",
                   coalesce(i.resolution = 'obsolete', false) AS "Moved",
                   h.label                      AS "HeldBy",
                   i.verify_cmd                 AS "VerifyCmd",
                   i.verify_manual              AS "VerifyManual",
                   coalesce(v.confirmed, 0)::int AS "Confirmations"
            FROM   item i
            JOIN   item_kind k ON k.key = i.kind
            LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
            LEFT   JOIN item p ON p.id = i.parent_id
            LEFT   JOIN LATERAL (
                     SELECT count(*) AS total, count(*) FILTER (WHERE ic.checked) AS ok
                     FROM   item_criterion ic WHERE ic.item_id = i.id) c ON true
            LEFT   JOIN LATERAL (
                     SELECT count(*) FILTER (WHERE vv.passed AND vv.confirmed_by IS NULL) AS passed,
                            count(*) FILTER (WHERE vv.passed AND vv.run_by = 'orchestrator') AS orch,
                            count(*) FILTER (WHERE vv.passed AND vv.confirmed_by IS NOT NULL) AS confirmed
                     FROM   verification vv WHERE vv.item_id = i.id) v ON true
            LEFT   JOIN LATERAL (
                     SELECT aa.value FROM artifact aa
                     WHERE  aa.item_id = i.id AND aa.kind = 'commit'
                     ORDER  BY aa.at DESC LIMIT 1) a ON true
            LEFT   JOIN LATERAL (
                     SELECT coalesce(hs.name, cc.agent_name) AS label
                     FROM   claim cc LEFT JOIN agent_session hs ON hs.id = cc.session_id
                     WHERE  cc.item_id = i.id AND cc.released_at IS NULL AND cc.expires_at >= now()
                     LIMIT  1) h ON true
            {WorkmanQueries.Rollup}
            WHERE  i.id = ANY(@ids);
            """;

        return await ListAsync<ItemFacts>(sql, new { ids = itemIds.ToArray() }, cancellationToken);
    }

    public async Task<IReadOnlyList<BlockerRow>> BlockerFindingsAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
        => [.. (await BlockerFindingsAsync([scopeItemId], cancellationToken))[scopeItemId]];

    /// <summary><see cref="BlockerFindingsAsync(Guid, CancellationToken)"/> for many scopes in one command.</summary>
    public async Task<ILookup<Guid, BlockerRow>> BlockerFindingsAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.ref   AS "FindingRef",
                    CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                           AS "QualifiedRef",
                    i.title AS "FindingTitle",
                    t.ref   AS "TargetRef",
                    i.id    AS "FindingId",
                    {Scoped.Column}
             FROM   item i
             {Scoped.JoinItem}
             LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             LEFT   JOIN item t ON t.id = i.parent_id
             WHERE  i.kind = 'finding'
               AND  i.severity = 'blocker'
               AND  i.status NOT IN ('done','cancelled','superseded')
             ORDER  BY i.ref;
             """;

        return await ScopedAsync<BlockerRow>(sql, new { scopes = Scoped.Ids(scopeItemIds) }, cancellationToken);
    }

    /// <summary>
    /// The <c>← 2.4</c> flag: open blockers, per blocked item, under any of the scopes. Several
    /// scopes because <c>work_get</c> renders items from unrelated subtrees in one call.
    /// </summary>
    public async Task<IReadOnlyList<BlockedRow>> BlockedByAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT i.id  AS "ItemId",
                   i.ref AS "Ref",
                   b.ref AS "BlockedByRef",
                   i.ref_scope_id AS "RefScopeId",
                   b.id AS "BlockedById",
                   b.ref_scope_id AS "BlockedByScopeId"
            FROM   item_link l
            JOIN   item i ON i.id = l.to_item_id
            JOIN   item b ON b.id = l.from_item_id
            WHERE  l.type = 'blocks'
              AND  b.status NOT IN ('done','cancelled','superseded')
              AND  (i.id = ANY(@scopes) OR i.ancestors && @scopes)
            ORDER  BY i.ref, b.ref;
            """;

        return await ListAsync<BlockedRow>(sql, new { scopes = scopeItemIds.ToArray() }, cancellationToken);
    }

    /// <summary>
    /// doc 07 §5 — a lapsed lease stays the top-ranked attention line (doc 06 §5) even after the
    /// sweep has released it, because the sweep answers *who holds this* and the line answers *who
    /// picks it up*. It clears itself the moment someone takes a live claim, so it never needs a
    /// time window to stop nagging.
    /// </summary>
    public async Task<IReadOnlyList<ClaimRow>> ExpiredClaimsAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
        => [.. (await ExpiredClaimsAsync([scopeItemId], cancellationToken))[scopeItemId]];

    /// <summary><see cref="ExpiredClaimsAsync(Guid, CancellationToken)"/> for many scopes in one command.</summary>
    public async Task<ILookup<Guid, ClaimRow>> ExpiredClaimsAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT "ItemRef", "QualifiedRef", "AgentName", "ExpiresAt", "Tree", item_id AS "ItemId", "ForScope" FROM (
               SELECT DISTINCT ON (sc.id, c.item_id)
                      i.ref        AS "ItemRef",
                      CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                                   AS "QualifiedRef",
                      coalesce(hs.name, c.agent_name) AS "AgentName",
                      c.expires_at AS "ExpiresAt",
                      c.tree       AS "Tree",
                      c.item_id,
                      {Scoped.Column}
               FROM   claim c
               JOIN   item i ON i.id = c.item_id
               {Scoped.JoinItem}
               LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
               LEFT   JOIN agent_session hs ON hs.id = c.session_id
               WHERE  c.expires_at < now()
                 AND  (c.released_at IS NULL OR c.release_reason = 'expired')
                 AND  i.status NOT IN ('done','cancelled','superseded')
                 AND  NOT EXISTS (SELECT 1 FROM claim live
                                  WHERE live.item_id = c.item_id
                                    AND live.released_at IS NULL
                                    AND live.expires_at >= now())
               ORDER  BY sc.id, c.item_id, c.expires_at DESC
             ) lapsed
             ORDER  BY "ExpiresAt";
             """;

        return await ScopedAsync<ClaimRow>(sql, new { scopes = Scoped.Ids(scopeItemIds) }, cancellationToken);
    }

    /// <summary>
    /// The watch floor's WORKING NOW block (doc 13): one row per claimed item, the live lease
    /// preferred over a lapsed one. Lapsed leases stay in the result because the block's job is
    /// "who is meant to be holding this", and a lease that ran out is the most interesting row on
    /// the board — <see cref="ExpiredClaimsAsync"/> answers the narrower question of what to nag
    /// about, and the two must not be collapsed.
    /// </summary>
    public async Task<IReadOnlyList<LiveClaimRow>> LiveClaimsAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT "ItemRef", "QualifiedRef", "Title", "AgentName", "ExpiresAt" FROM (
               SELECT DISTINCT ON (c.item_id)
                      i.ref        AS "ItemRef",
                      CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                                   AS "QualifiedRef",
                      i.title      AS "Title",
                      coalesce(hs.name, c.agent_name) AS "AgentName",
                      c.expires_at AS "ExpiresAt",
                      c.item_id
               FROM   claim c
               JOIN   item i ON i.id = c.item_id
               LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
               LEFT   JOIN agent_session hs ON hs.id = c.session_id
               WHERE  (c.released_at IS NULL OR c.release_reason = 'expired')
                 AND  i.status NOT IN ('done','cancelled','superseded')
                 AND  {InScope}
               ORDER  BY c.item_id, (c.released_at IS NULL) DESC, c.expires_at DESC
             ) held
             ORDER  BY "ExpiresAt" DESC;
             """;

        return await ListAsync<LiveClaimRow>(sql, new { scope = scopeItemId }, cancellationToken);
    }

    /// <summary><c>in_progress</c> with nothing holding it, older than the threshold.</summary>
    public async Task<IReadOnlyList<StaleRow>> StaleInProgressAsync(
        Guid scopeItemId,
        int hours,
        CancellationToken cancellationToken = default)
        => [.. (await StaleInProgressAsync([scopeItemId], hours, cancellationToken))[scopeItemId]];

    /// <summary><see cref="StaleInProgressAsync(Guid, int, CancellationToken)"/> for many scopes in one command.</summary>
    public async Task<ILookup<Guid, StaleRow>> StaleInProgressAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        int hours,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.ref        AS "Ref",
                    CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                                 AS "QualifiedRef",
                    i.started_at AS "StartedAt",
                    i.id         AS "Id",
                    {Scoped.Column}
             FROM   item i
             {Scoped.JoinItem}
             LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             WHERE  i.status = 'in_progress'
               AND  i.started_at IS NOT NULL
               AND  i.started_at < now() - make_interval(hours => @hours)
               AND  NOT EXISTS (SELECT 1 FROM claim c
                                WHERE c.item_id = i.id AND c.released_at IS NULL AND c.expires_at >= now())
             ORDER  BY i.started_at;
             """;

        return await ScopedAsync<StaleRow>(sql, new { scopes = Scoped.Ids(scopeItemIds), hours }, cancellationToken);
    }

    /// <summary>
    /// doc 07 §2 — phases whose every task is closed and which are not themselves closed. The store
    /// deliberately runs no transition for this (FINDINGS-live F-L1): a phase with nothing left to
    /// do is <em>ready to be gated</em>, and auto-closing it would delete the composition review.
    /// The obligation is surfaced instead, which is what doc 06 §5's attention block is for.
    /// </summary>
    public async Task<IReadOnlyList<GateRow>> UngatedPhasesAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
        => [.. (await UngatedPhasesAsync([scopeItemId], cancellationToken))[scopeItemId]];

    /// <summary><see cref="UngatedPhasesAsync(Guid, CancellationToken)"/> for many scopes in one command.</summary>
    public async Task<ILookup<Guid, GateRow>> UngatedPhasesAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT p.ref                 AS "PhaseRef",
                    CASE WHEN s.ref IS NULL THEN p.ref ELSE s.ref || '/' || p.ref END
                                          AS "QualifiedRef",
                    count(*)::int         AS "TaskCount",
                    {Scoped.Column}
             FROM   item p
             JOIN   {Scoped.Scopes} ON (p.id = sc.id OR p.ancestors @> ARRAY[sc.id]::uuid[])
             JOIN   item c ON c.ancestors @> ARRAY[p.id]::uuid[] AND c.kind IN ('task','subtask')
             LEFT   JOIN item s ON s.id = p.ref_scope_id AND s.id <> p.id
             WHERE  p.kind = 'phase'
               AND  p.status NOT IN ('done','cancelled','superseded')
             GROUP  BY sc.id, p.id, p.ref, p.seq, s.ref
             HAVING count(*) FILTER (
                      WHERE c.status NOT IN ('done','cancelled','superseded')) = 0
             ORDER  BY p.seq;
             """;

        return await ScopedAsync<GateRow>(sql, new { scopes = Scoped.Ids(scopeItemIds) }, cancellationToken);
    }

    /// <summary>
    /// Closed without the orchestrator verification doc 07 §3 condition 2 demands. U4 makes this
    /// unwritable; until then it is the highest-value thing an audit can say about finished work.
    /// <para>
    /// A deferral closes its item <c>done/obsolete</c> without the guard, because the work moved
    /// rather than finished (doc 07 §4). Verifying it would be a false claim, so it is not counted.
    /// <see cref="ItemFacts.Moved"/> applies the same rule to the row flag.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<UnverifiedRow>> DoneUnverifiedAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
        => [.. (await DoneUnverifiedAsync([scopeItemId], cancellationToken))[scopeItemId]];

    /// <summary><see cref="DoneUnverifiedAsync(Guid, CancellationToken)"/> for many scopes in one command.</summary>
    public async Task<ILookup<Guid, UnverifiedRow>> DoneUnverifiedAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.ref AS "Ref",
                    CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                         AS "QualifiedRef",
                    i.id        AS "Id",
                    i.closed_at AS "ClosedAt",
                    {Scoped.Column}
             FROM   item i
             {Scoped.JoinItem}
             JOIN   item_kind k ON k.key = i.kind
             LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             WHERE  i.status = 'done'
               AND  i.resolution <> 'obsolete'
               AND  k.requires_verify
               AND  NOT EXISTS (SELECT 1 FROM verification v
                                WHERE v.item_id = i.id AND v.passed AND v.run_by = 'orchestrator')
               AND  NOT EXISTS (SELECT 1 FROM verification c
                                WHERE c.item_id = i.id AND c.passed AND c.confirmed_by IS NOT NULL)
             ORDER  BY i.closed_at DESC NULLS LAST, i.ref;
             """;

        return await ScopedAsync<UnverifiedRow>(sql, new { scopes = Scoped.Ids(scopeItemIds) }, cancellationToken);
    }

    /// <summary>
    /// C343 — done on a person's confirmation and on nothing an orchestrator ran: its own count, never a verified close
    /// and never one <see cref="DoneUnverifiedAsync(Guid, CancellationToken)"/> lists. A deferral is left out, as there.
    /// </summary>
    public async Task<IReadOnlyList<ConfirmedRow>> DoneConfirmedAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
        => [.. (await DoneConfirmedAsync([scopeItemId], cancellationToken))[scopeItemId]];

    /// <summary><see cref="DoneConfirmedAsync(Guid, CancellationToken)"/> for many scopes in one command.</summary>
    public async Task<ILookup<Guid, ConfirmedRow>> DoneConfirmedAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT i.ref AS "Ref",
                    CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END
                         AS "QualifiedRef",
                    i.id        AS "Id",
                    i.closed_at AS "ClosedAt",
                    c.confirmed_by AS "ConfirmedBy",
                    c.quote        AS "Quote",
                    {Scoped.Column}
             FROM   item i
             {Scoped.JoinItem}
             JOIN   item_kind k ON k.key = i.kind
             LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             JOIN   LATERAL (
                      SELECT cv.confirmed_by, cv.quote FROM verification cv
                      WHERE  cv.item_id = i.id AND cv.passed AND cv.confirmed_by IS NOT NULL
                      ORDER  BY cv.at DESC, cv.id DESC LIMIT 1) c ON true
             WHERE  i.status = 'done'
               AND  i.resolution <> 'obsolete'
               AND  k.requires_verify
               AND  NOT EXISTS (SELECT 1 FROM verification v
                                WHERE v.item_id = i.id AND v.passed AND v.run_by = 'orchestrator')
             ORDER  BY i.closed_at DESC NULLS LAST, i.ref;
             """;

        return await ScopedAsync<ConfirmedRow>(sql, new { scopes = Scoped.Ids(scopeItemIds) }, cancellationToken);
    }

    /// <summary>
    /// The journal after a cursor. <paramref name="oldestFirst"/> is not a formatting preference:
    /// with <c>LIMIT</c> applied, descending returns the <em>newest</em> page after the cursor and
    /// silently drops everything between, so a client catching up from its last seq would lose
    /// exactly the events it reconnected to collect. Ascending makes the page contiguous from the
    /// cursor, which is what makes the stream's monotonic seq worth having.
    /// </summary>
    public async Task<IReadOnlyList<EventRow>> EventsAsync(
        Guid workspaceId,
        Guid? scopeItemId,
        long sinceSeq,
        int limit,
        bool oldestFirst = false,
        CancellationToken cancellationToken = default,
        Guid? home = null,
        long? beforeSeq = null)
    {
        var sql =
            $"""
             SELECT e.seq     AS "Seq",
                    {ShownRef("i")} AS "ItemRef",
                    e.at      AS "At",
                    coalesce(es.name, e.actor) AS "Actor",
                    e.role    AS "Role",
                    e.kind    AS "Kind",
                    e.summary AS "Summary"
             FROM   event e
             LEFT   JOIN item i ON i.id = e.item_id
             LEFT   JOIN agent_session es ON es.id = e.session_id
             WHERE  e.workspace_id = @workspace
               AND  e.seq > @since
               AND  (@before::bigint IS NULL OR e.seq < @before)
               AND  (@scope IS NULL
                     OR e.item_id = @scope
                     OR EXISTS (SELECT 1 FROM item x
                                WHERE x.id = e.item_id AND x.ancestors @> ARRAY[@scope]::uuid[]))
             ORDER  BY e.seq {(oldestFirst ? "ASC" : "DESC")}
             LIMIT  @limit;
             """;

        return await ListAsync<EventRow>(
            sql,
            new { workspace = workspaceId, scope = scopeItemId, since = sinceSeq, limit, home, before = beforeSeq },
            cancellationToken);
    }

    public async Task<IReadOnlyList<CriterionRow>> CriteriaAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT i.id       AS "ItemId",
                   i.ref      AS "ItemRef",
                   c.seq      AS "Seq",
                   c.text     AS "Text",
                   c.checked  AS "Checked",
                   c.evidence AS "Evidence"
            FROM   item_criterion c
            JOIN   item i ON i.id = c.item_id
            WHERE  c.item_id = ANY(@ids)
            ORDER  BY i.id, c.seq;
            """;

        return await ListAsync<CriterionRow>(sql, new { ids = itemIds.ToArray() }, cancellationToken);
    }

    public async Task<IReadOnlyList<LinkRow>> LinksAsync(
        IReadOnlyCollection<Guid> itemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT f.id     AS "FromId",
                   t.id     AS "ToId",
                   f.ref    AS "FromRef",
                   t.ref    AS "ToRef",
                   l.type   AS "Type",
                   f.status AS "FromStatus",
                   f.ref_scope_id AS "FromScopeId",
                   t.ref_scope_id AS "ToScopeId"
            FROM   item_link l
            JOIN   item f ON f.id = l.from_item_id
            JOIN   item t ON t.id = l.to_item_id
            WHERE  l.from_item_id = ANY(@ids) OR l.to_item_id = ANY(@ids)
            ORDER  BY f.ref, t.ref;
            """;

        return await ListAsync<LinkRow>(sql, new { ids = itemIds.ToArray() }, cancellationToken);
    }

    /// <summary>
    /// doc 01 §5 check #1: a <c>verify_cmd</c> this workspace has never recorded red. That is the
    /// "filter matches only tests that already pass" defect, which no file-based audit can see.
    /// An item whose check is marked manual (<c>verify_manual</c>, set explicitly — C345) has no command to see red.
    /// </summary>
    public async Task<IReadOnlyList<AuditRow>> NeverRedAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default,
        Guid? home = null)
    {
        var sql =
            $"""
             SELECT 'verify-never-red'                                  AS "Check",
                    {ShownRef("i")}                                     AS "Ref",
                    'verify_cmd has never been recorded red: ' || i.verify_cmd AS "Problem",
                    'work_verify(ref:"' || {ShownRef("i")} || '", command:"' || i.verify_cmd
                      || '", result_line:"<the red line>", passed:false, run_by:"orchestrator")' AS "Route"
             FROM   item i
             WHERE  i.verify_cmd IS NOT NULL
               AND  NOT i.verify_manual
               AND  i.status NOT IN ('done','cancelled','superseded')
               AND  {InScope}
               AND  NOT EXISTS (SELECT 1 FROM verification v
                                WHERE v.workspace_id = i.workspace_id
                                  AND v.command = i.verify_cmd
                                  AND NOT v.passed)
             ORDER  BY i.ref;
             """;

        return await ListAsync<AuditRow>(sql, new { scope = scopeItemId, home }, cancellationToken);
    }

    /// <summary>
    /// doc 07 §3 — a <c>force</c> that leaves no mark is worse than no override at all, so every
    /// forced closure is permanently visible here. This is the check that makes the escape hatch
    /// honest rather than silent.
    /// </summary>
    public async Task<IReadOnlyList<AuditRow>> ForcedTransitionsAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default,
        Guid? home = null)
        => [.. (await ForcedTransitionsAsync([scopeItemId], cancellationToken, home))[scopeItemId]];

    /// <summary><see cref="ForcedTransitionsAsync(Guid, CancellationToken)"/> for many scopes in one command.</summary>
    public async Task<ILookup<Guid, AuditRow>> ForcedTransitionsAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default,
        Guid? home = null)
    {
        var sql =
            $"""
             SELECT 'forced-closure'                                     AS "Check",
                    {ShownRef("i")}                                      AS "Ref",
                    'closed with force:true — '
                      || coalesce(nullif(e.payload->>'reason', ''), 'no reason recorded') AS "Problem",
                    'work_verify(ref:"' || {ShownRef("i")} || '", result_line:"…", run_by:"orchestrator") to '
                      || 'record the evidence it closed without, or work_note(kind:"decision", '
                      || 'body:"…") to accept it on the record'                            AS "Route",
                    {Scoped.Column}
             FROM   event e
             JOIN   item i ON i.id = e.item_id
             {Scoped.JoinItem}
             WHERE  e.kind = 'forced_transition'
             ORDER  BY e.seq DESC;
             """;

        return await ScopedAsync<AuditRow>(sql, new { scopes = Scoped.Ids(scopeItemIds), home }, cancellationToken);
    }

    /// <summary>doc 01 §5 check #5: a superseded item that open work still points at.</summary>
    public async Task<IReadOnlyList<AuditRow>> SupersededStillReferencedAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default,
        Guid? home = null)
    {
        var sql =
            $"""
            SELECT 'superseded-referenced'                              AS "Check",
                   {ShownRef("s")}                                      AS "Ref",
                   'superseded, but ' || {ShownRef("o")} || ' still links to it'   AS "Problem",
                   'work_link(from:"' || {ShownRef("o")} || '", to:"<the replacement>", type:"blocks")' AS "Route"
            FROM   item_link l
            JOIN   item s ON s.id = l.to_item_id
            JOIN   item o ON o.id = l.from_item_id
            WHERE  s.status = 'superseded'
              AND  o.status NOT IN ('done','cancelled','superseded')
              AND  (s.id = @scope OR s.ancestors @> ARRAY[@scope]::uuid[])
            ORDER  BY s.ref;
            """;

        return await ListAsync<AuditRow>(sql, new { scope = scopeItemId, home }, cancellationToken);
    }

    /// <summary>doc 01 §5 check #6: an upcoming risk no active constraint covers.</summary>
    public async Task<IReadOnlyList<AuditRow>> UncoveredRiskAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default,
        Guid? home = null)
    {
        var sql =
            $"""
             SELECT 'risk-uncovered'                                   AS "Check",
                    {ShownRef("i")}                                    AS "Ref",
                    'risk_tag "' || t.tag || '" has no active constraint covering it' AS "Problem",
                    'work_note(kind:"decision", body:"…") or record a constraint — '
                      || 'no active constraint_rule lists "' || t.tag || '" in covers_risk' AS "Route"
             FROM   item i
             CROSS  JOIN LATERAL unnest(i.risk_tags) AS t(tag)
             WHERE  i.status IN ('open','ready','in_progress','blocked','review')
               AND  {InScope}
               AND  NOT EXISTS (SELECT 1 FROM constraint_rule c
                                WHERE c.workspace_id = i.workspace_id
                                  AND c.active
                                  AND c.covers_risk @> ARRAY[t.tag])
             ORDER  BY i.ref, t.tag;
             """;

        return await ListAsync<AuditRow>(sql, new { scope = scopeItemId, home }, cancellationToken);
    }

    /// <summary>A prohibition with no way out — doc 02 §8's <c>route</c> column, audited.</summary>
    public async Task<IReadOnlyList<AuditRow>> RouteLessConstraintsAsync(
        Guid workspaceId,
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            """
            SELECT 'constraint-no-route'                             AS "Check",
                   c.ref                                             AS "Ref",
                   'binding rule with no route: ' || c.text          AS "Problem",
                   'record the way out — a rule demanding a capability the agent lacks is a trap' AS "Route"
            FROM   constraint_rule c
            CROSS  JOIN (SELECT id, ancestors FROM item WHERE id = @scope) x
            WHERE  c.workspace_id = @workspace
              AND  c.active
              AND  c.kind = 'red_line'
              AND  (c.route IS NULL OR c.route = '')
              AND  (c.scope_item_id IS NULL OR c.scope_item_id = x.id OR c.scope_item_id = ANY(x.ancestors))
            ORDER  BY c.created_at;
            """;

        return await ListAsync<AuditRow>(
            sql,
            new { workspace = workspaceId, scope = scopeItemId },
            cancellationToken);
    }

    /// <summary>
    /// mcp-fit/1. <paramref name="alias"/>'s ref as a reader of <c>@home</c> (a ref scope) needs it: bare inside
    /// that scope and for a run or project, else run-qualified, <c>run/ref</c>. A bare <c>2</c> in a
    /// project's audit or history named no run, and the same <c>2</c> sits in each.
    /// </summary>
    internal static string ShownRef(string alias)
        => $"(CASE WHEN {alias}.ref_scope_id IS NULL OR {alias}.ref_scope_id = {alias}.id "
           + $"OR {alias}.ref_scope_id = @home::uuid THEN {alias}.ref "
           + $"ELSE (SELECT rs.ref FROM item rs WHERE rs.id = {alias}.ref_scope_id) || '/' || {alias}.ref END)";

    private const string ScopeColumns =
        """
        i.id           AS "Id",
        i.workspace_id AS "WorkspaceId",
        i.ref          AS "Ref",
        i.kind         AS "Kind",
        i.title        AS "Title",
        i.intent       AS "Intent",
        i.status       AS "Status",
        i.ref_scope_id AS "RefScopeId",
        i.parent_id    AS "ParentId",
        CASE WHEN s.ref IS NULL THEN i.ref ELSE s.ref || '/' || i.ref END AS "QualifiedRef"
        """;

    /// <summary>
    /// <c>project/run/ref</c> (a run's is <c>project/run</c>, a project's its ref) over <c>i</c>, its
    /// ref scope <c>s</c> and <see cref="NearestProject"/> <c>pj</c>. The v1 API's <c>fullRef</c> and
    /// the tools' <c>web</c> line both print it, and <c>/r</c> resolves it back.
    /// </summary>
    internal const string FullRefColumn =
        """
        CASE WHEN pj.ref IS NULL OR pj.id = s.id
             THEN coalesce(s.ref || '/', '') || i.ref
             ELSE pj.ref || '/' || coalesce(s.ref || '/', '') || i.ref END
        """;

    internal const string NearestProject =
        """
        LEFT   JOIN LATERAL (
                   SELECT a.id, a.ref FROM item a
                   WHERE  a.kind = 'project' AND a.id = ANY(i.ancestors)
                   ORDER  BY array_position(i.ancestors, a.id) DESC
                   LIMIT  1) pj ON true
        """;

    public Task<string?> FullRefAsync(Guid itemId, CancellationToken cancellationToken = default)
        => SingleAsync<string>(
            $"""
             SELECT {FullRefColumn}
             FROM   item i
             LEFT   JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
             {NearestProject}
             WHERE  i.id = @id;
             """,
            new { id = itemId },
            cancellationToken);

    /// <summary>
    /// Npgsql reads a <c>timestamptz</c> back as a UTC <see cref="DateTime"/>, and Dapper matches a
    /// record constructor by the reader's field types — so without this, every record carrying a
    /// <see cref="DateTimeOffset"/> fails to materialise at all rather than converting.
    /// </summary>
    internal sealed class TimestampHandler : SqlMapper.TypeHandler<DateTimeOffset>
    {
        public override DateTimeOffset Parse(object value) => value switch
        {
            DateTimeOffset offset => offset,
            DateTime local => new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Utc)),
            _ => (DateTimeOffset)value,
        };

        public override void SetValue(IDbDataParameter parameter, DateTimeOffset value)
            => parameter.Value = value.UtcDateTime;
    }

    /// <summary>
    /// The fourth honesty number. Unlike the other three this one is not an accusation — a deferral
    /// that became a tracked follow-up is the system working, and doc 07 §4 counts it as a move
    /// rather than a failure. It is here because a strip that only ever shows what went wrong
    /// teaches people to stop reading it.
    /// </summary>
    public async Task<int> OpenFollowupsAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
        => (await OpenFollowupsAsync([scopeItemId], cancellationToken)).GetValueOrDefault(scopeItemId);

    /// <summary><see cref="OpenFollowupsAsync(Guid, CancellationToken)"/> for many scopes in one command; a scope with none is absent.</summary>
    public async Task<IReadOnlyDictionary<Guid, int>> OpenFollowupsAsync(
        IReadOnlyCollection<Guid> scopeItemIds,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT sc.id AS "Scope", count(*)::int AS "Count"
             FROM   item i
             {Scoped.JoinItem}
             WHERE  i.kind = 'followup'
               AND  i.status NOT IN ('done','cancelled','superseded')
             GROUP  BY sc.id;
             """;

        return (await ListAsync<ScopeCount>(sql, new { scopes = Scoped.Ids(scopeItemIds) }, cancellationToken))
            .ToDictionary(c => c.Scope, c => c.Count);
    }

    /// <summary>
    /// Open items derived from the scope or anything beneath it, wherever they now sit: a deferral
    /// moves work out of the subtree (doc 07 §4), and the backlog is usually under the project.
    /// </summary>
    public async Task<IReadOnlyList<ItemFacts>> OpenDerivedAsync(
        Guid scopeItemId,
        CancellationToken cancellationToken = default)
    {
        const string sql =
            $"""
             SELECT DISTINCT d.id, d.seq
             FROM   item_link l
             JOIN   item i ON i.id = l.to_item_id
             JOIN   item d ON d.id = l.from_item_id
             WHERE  l.type = 'derived_from'
               AND  d.status NOT IN ('done','cancelled','superseded')
               AND  {InScope}
             ORDER  BY d.seq, d.id;
             """;

        var ids = await ListAsync<DerivedId>(sql, new { scope = scopeItemId }, cancellationToken);
        if (ids.Count == 0)
        {
            return [];
        }

        var facts = (await FactsAsync([.. ids.Select(i => i.Id)], cancellationToken)).ToDictionary(f => f.Id);
        return [.. ids.Select(i => facts[i.Id])];
    }

    private sealed record DerivedId(Guid Id, decimal Seq);

    private async Task<T?> SingleAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
    {
        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var connection = tenant.Connection;
        return await connection.QuerySingleOrDefaultAsync<T>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
    }

    private Task<ILookup<Guid, T>> ScopedAsync<T>(string sql, object parameters, CancellationToken cancellationToken)
        => Scoped.ListAsync<T>(_connections, sql, parameters, cancellationToken);

    private async Task<IReadOnlyList<T>> ListAsync<T>(
        string sql,
        object parameters,
        CancellationToken cancellationToken)
    {
        await using var tenant = await _connections.TenantAsync(cancellationToken);
        var connection = tenant.Connection;
        var rows = await connection.QueryAsync<T>(
            new CommandDefinition(sql, parameters, cancellationToken: cancellationToken));
        return rows.AsList();
    }
}
