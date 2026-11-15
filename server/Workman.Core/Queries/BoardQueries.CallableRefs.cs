namespace Workman.Core.Queries;

public sealed partial class BoardQueries
{
    /// <summary>
    /// <see cref="CallableRefAsync"/> for many items in two commands, whatever their number: the same forms tried in
    /// the same order, each resolved by <see cref="ResolveAsync(Guid, string, CancellationToken)"/>'s predicate. One
    /// read lists every item's parent and project ref, a second resolves every distinct form at once. The single-item
    /// method stays what the MCP tools print; the v1 API batches through this one.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, string>> CallableRefsAsync(
        IReadOnlyCollection<(ScopeRef Item, bool RefShared)> items,
        CancellationToken cancellationToken = default)
    {
        var callable = new Dictionary<Guid, string>();
        var shared = new List<ScopeRef>();
        foreach (var (item, refShared) in items)
        {
            if (refShared)
            {
                shared.Add(item);
            }
            else
            {
                callable[item.Id] = item.Ref;
            }
        }

        if (shared.Count == 0)
        {
            return callable;
        }

        var context = (await ListAsync<FormContext>(
                $"""
                 SELECT i.id AS "Id",
                        (SELECT p.ref FROM item p WHERE p.id = i.parent_id) AS "ParentRef",
                        ({NearestProjectRef}) AS "ProjectRef"
                 FROM   item i
                 WHERE  i.id = ANY(@ids);
                 """,
                new { ids = shared.Select(s => s.Id).Distinct().ToArray() },
                cancellationToken))
            .ToDictionary(c => c.Id);

        // Every item's forms in CallableRefAsync's order: its qualified ref first when it has a scope of its own,
        // then UniqueRefAsync's list.
        var tries = shared.ToDictionary(s => s.Id, s => Forms(s, context.GetValueOrDefault(s.Id)));

        var lookups = new List<FormLookup>();
        var index = new Dictionary<FormLookup, int>();
        foreach (var form in tries.Values.SelectMany(f => f).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var lookup in Lookups(form))
            {
                if (index.TryAdd(lookup, lookups.Count))
                {
                    lookups.Add(lookup);
                }
            }
        }

        var workspaces = shared.Select(s => s.WorkspaceId).Distinct().ToList();
        var matches = new Dictionary<(Guid Workspace, int Lookup), List<Guid>>();
        if (lookups.Count > 0)
        {
            foreach (var hit in await ListAsync<FormMatch>(
                         $"""
                          SELECT w.id AS "WorkspaceId", f.n::int AS "Lookup", i.id AS "Id"
                          FROM   unnest(@workspaces) AS w(id)
                          CROSS  JOIN unnest(@projects, @qualifiers, @leaves) WITH ORDINALITY AS f(project, qualifier, leaf, n)
                          JOIN   item i ON i.workspace_id = w.id AND lower(i.ref) = lower(f.leaf)
                          LEFT JOIN item s ON s.id = i.ref_scope_id AND s.id <> i.id
                          LEFT JOIN item p ON p.id = i.parent_id
                          JOIN   item_kind k ON k.key = i.kind
                          WHERE  (f.qualifier IS NULL
                                  OR lower(coalesce(s.ref, i.ref)) = lower(f.qualifier)
                                  OR (i.ref_scope_id = i.id AND lower(p.ref) = lower(f.qualifier)))
                            AND  (f.project IS NULL
                                  OR EXISTS (SELECT 1 FROM item pj
                                             WHERE  pj.id = ANY(i.ancestors)
                                               AND  pj.kind = 'project'
                                               AND  lower(pj.ref) = lower(f.project)));
                          """,
                         new
                         {
                             workspaces = workspaces.ToArray(),
                             projects = lookups.Select(l => l.Project).ToArray(),
                             qualifiers = lookups.Select(l => l.Qualifier).ToArray(),
                             leaves = lookups.Select(l => l.Leaf).ToArray(),
                         },
                         cancellationToken))
            {
                var key = (hit.WorkspaceId, hit.Lookup - 1);
                if (!matches.TryGetValue(key, out var ids))
                {
                    matches[key] = ids = [];
                }

                ids.Add(hit.Id);
            }
        }

        IReadOnlyList<Guid> Resolve(Guid workspace, string form)
        {
            IReadOnlyList<Guid> Of(FormLookup lookup) => matches.GetValueOrDefault((workspace, index[lookup])) ?? [];

            var all = Lookups(form);
            return all switch
            {
                [] => [],
                [var only] => Of(only),
                [var byRun, var byProject] => Of(byRun) is { Count: > 0 } run ? run : Of(byProject),
                _ => throw new InvalidOperationException("a form resolves one way or two"),
            };
        }

        foreach (var item in shared)
        {
            var qualified = item.QualifiedRef ?? item.Ref;
            callable[item.Id] = tries[item.Id].FirstOrDefault(form => Resolve(item.WorkspaceId, form) is [var only] && only == item.Id)
                ?? qualified;
        }

        return callable;
    }

    private const string NearestProjectRef =
        """
        SELECT pj.ref
        FROM   item pj
        WHERE  pj.id = ANY(i.ancestors) AND pj.kind = 'project'
        ORDER  BY array_position(i.ancestors, pj.id) DESC
        LIMIT  1
        """;

    /// <summary><see cref="CallableRefAsync"/>'s qualified try, then <see cref="UniqueRefAsync"/>'s forms, in order.</summary>
    private static List<string> Forms(ScopeRef item, FormContext? context)
    {
        var forms = new List<string>();
        if (item.RefScopeId != item.Id)
        {
            forms.Add(item.QualifiedRef ?? item.Ref);
        }

        var unique = new List<string> { item.Ref };
        var qualified = item.QualifiedRef is { Length: > 0 } q ? q : item.Ref;
        unique.Add(qualified);

        if (item.RefScopeId == item.Id)
        {
            if (item.ParentId is not null && context?.ParentRef is { Length: > 0 } parent)
            {
                unique.Add($"{parent}/{item.Ref}");
            }

            unique.Add($"{item.Ref}/{item.Ref}");
        }

        if (context?.ProjectRef is { Length: > 0 } project)
        {
            unique.Add($"{project}/{item.Ref}");
            unique.Add($"{project}/{qualified}");
        }

        forms.AddRange(unique.Distinct(StringComparer.OrdinalIgnoreCase));
        return forms;
    }

    /// <summary>
    /// The lookups <see cref="ResolveAsync(Guid, string, CancellationToken)"/> makes for a form, lower-cased: none for
    /// a malformed one, two for <c>a/b</c> (as a run, else as a project), one otherwise.
    /// </summary>
    private static IReadOnlyList<FormLookup> Lookups(string reference)
    {
        var parts = reference.ToLowerInvariant().Split('/');
        if (parts.Length > 3 || (parts.Length > 1 && parts.Any(p => p.Length == 0)))
        {
            return [];
        }

        return parts switch
        {
            [var leaf] => [new FormLookup(null, null, leaf)],
            [var project, var qualifier, var leaf] => [new FormLookup(project, qualifier, leaf)],
            [var qualifier, var leaf] => [new FormLookup(null, qualifier, leaf), new FormLookup(qualifier, null, leaf)],
            _ => [],
        };
    }

    private sealed record FormLookup(string? Project, string? Qualifier, string Leaf);

    private sealed record FormContext(Guid Id, string? ParentRef, string? ProjectRef);

    private sealed record FormMatch(Guid WorkspaceId, int Lookup, Guid Id);
}
