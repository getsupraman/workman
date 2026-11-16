using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Core.Rendering;
using Workman.Core.Writing;

namespace Workman.Core.Board;

public sealed partial class BoardReader
{
    /// <summary>The prompt actions that apply to a scope (<see cref="PromptCatalogue"/>), in catalogue order.</summary>
    public async Task<IReadOnlyList<PromptAction>> PromptsAsync(
        ScopeResult.Found scope,
        CancellationToken cancellationToken = default)
        => (await PromptsAsync(scope.WorkspaceId, [scope.Item], cancellationToken))[scope.Item.Id];

    /// <summary>
    /// <see cref="PromptsAsync(ScopeResult.Found, CancellationToken)"/> for many scopes of one workspace, in a fixed
    /// number of commands whatever their number: each fact is read for every scope at once.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<PromptAction>>> PromptsAsync(
        Guid workspaceId,
        IReadOnlyCollection<ScopeRef> scopes,
        CancellationToken cancellationToken = default)
        => (await PromptFactsAsync(workspaceId, scopes, cancellationToken))
            .ToDictionary(f => f.Key, f => PromptCatalogue.For(f.Value));

    public async Task<PromptFacts> PromptFactsAsync(ScopeResult.Found scope, CancellationToken cancellationToken = default)
        => (await PromptFactsAsync(scope.WorkspaceId, [scope.Item], cancellationToken))[scope.Item.Id];

    /// <summary>The facts the catalogue reads, for every scope, keyed by its id.</summary>
    public async Task<IReadOnlyDictionary<Guid, PromptFacts>> PromptFactsAsync(
        Guid workspaceId,
        IReadOnlyCollection<ScopeRef> scopes,
        CancellationToken cancellationToken = default)
    {
        var items = scopes.DistinctBy(s => s.Id).ToList();
        if (items.Count == 0)
        {
            return new Dictionary<Guid, PromptFacts>();
        }

        var callable = await CallablesAsync(items, cancellationToken);
        var projects = items.Where(i => i.Kind == "project").ToList();
        var runs = items.Where(i => i.Kind == "run").ToList();
        var projectIds = projects.Select(p => p.Id).ToList();

        var projectRuns = projectIds.Count == 0
            ? Array.Empty<ProjectRunRow>().ToLookup(_ => Guid.Empty)
            : await board.ProjectRunsAsync(projectIds, WorkWriter.BacklogRunTitle, cancellationToken);
        var backlogRuns = projects.ToDictionary(p => p.Id, p => projectRuns[p.Id].FirstOrDefault(r => !r.Closed && r.Backlog));
        var work = projects.ToDictionary(p => p.Id, p => projectRuns[p.Id].Where(r => !r.Backlog).ToList());

        ILookup<Guid, BacklogItemRow> backlog = Array.Empty<BacklogItemRow>().ToLookup(_ => Guid.Empty);
        IReadOnlyDictionary<Guid, string> backlogRefs = new Dictionary<Guid, string>();
        IReadOnlyDictionary<Guid, DateTimeOffset> archived = new Dictionary<Guid, DateTimeOffset>();
        IReadOnlyDictionary<Guid, int> unclosed = new Dictionary<Guid, int>();
        if (projects.Count > 0)
        {
            backlog = await board.ProjectBacklogAsync(
                [.. projects.Select(p => (p.Id, backlogRuns[p.Id]?.Id))], cancellationToken);
            backlogRefs = await CallablesAsync(
                [.. backlogRuns.Values.OfType<ProjectRunRow>().Select(r => r.Scope)], cancellationToken);
            archived = await board.ArchivedAtAsync(projectIds, cancellationToken);
            unclosed = await UnclosedCountAsync(workspaceId, projectIds, cancellationToken);
        }

        // One subtree read: every project and run asked for, and every open run a project's finalize waits on.
        var openWork = work.Values.SelectMany(w => w.Where(r => !r.Closed).Select(r => r.Id));
        var subtrees = await queries.SubtreeStatusesAsync(
            [.. projectIds, .. runs.Select(r => r.Id), .. openWork], cancellationToken);
        var leaves = subtrees.ToDictionary(g => g.Key, g => g.Where(n => Leaf(n.Kind)).ToList());
        IReadOnlyList<SubtreeStatus> LeavesOf(Guid id) => leaves.GetValueOrDefault(id) ?? [];

        var rules = projects.Count + runs.Count == 0
            ? new Dictionary<Guid, int>()
            : await board.RulesInScopeCountAsync(
                workspaceId, [.. projectIds, .. runs.Select(r => r.Id)], cancellationToken);

        var facts = new Dictionary<Guid, PromptFacts>();
        foreach (var item in items)
        {
            var reference = callable[item.Id];
            facts[item.Id] = item.Kind switch
            {
                "project" => ProjectPromptFacts(item, reference),
                "run" => RunPromptFacts(item, reference),
                _ => new PromptFacts(PromptScope.Item, reference, item.Kind, Glyph.Flat(item.Title), item.Status),
            };
        }

        return facts;

        PromptFacts RunPromptFacts(ScopeRef item, string reference)
        {
            var under = LeavesOf(item.Id);
            return new PromptFacts(
                PromptScope.Run,
                reference,
                item.Kind,
                Glyph.Flat(item.Title),
                item.Status,
                DoneLeaves: under.Count(n => n.Status == ItemStatus.Done),
                Open: under.Count(n => !PromptCatalogue.Closed(n.Status)),
                Blocked: under.Count(n => n.Status == ItemStatus.Blocked),
                Rules: rules.GetValueOrDefault(item.Id));
        }

        // Finalize applies once no run but the backlog has an unclosed leaf under it — a run or phase still open
        // over closed work is what the finalize prompt closes — and never once archived.
        PromptFacts ProjectPromptFacts(ScopeRef project, string reference)
        {
            var runsOf = work[project.Id];
            var finalizable = runsOf.Count > 0
                && !archived.ContainsKey(project.Id)
                && runsOf.Where(r => !r.Closed).All(r => !LeavesOf(r.Id).Any(n => !PromptCatalogue.Closed(n.Status)));
            var backlogRun = backlogRuns[project.Id];

            return new PromptFacts(
                PromptScope.Project,
                reference,
                project.Kind,
                Glyph.Flat(project.Title),
                project.Status,
                Unclosed: unclosed.GetValueOrDefault(project.Id),
                Backlog: backlog[project.Id].Count(),
                BacklogRef: backlogRun is null ? null : backlogRefs[backlogRun.Id],
                Finalizable: finalizable,
                Open: LeavesOf(project.Id).Count(n => !PromptCatalogue.Closed(n.Status)),
                Rules: rules.GetValueOrDefault(project.Id));
        }
    }

    /// <summary><see cref="CallableAsync(ScopeRef, CancellationToken)"/> for many items, in a fixed number of commands.</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> CallablesAsync(
        IReadOnlyCollection<ScopeRef> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var shared = (await board.FactsAsync([.. items.Select(i => i.Id)], cancellationToken))
            .Where(f => f.RefShared)
            .Select(f => f.Id)
            .ToHashSet();
        return await board.CallableRefsAsync([.. items.Select(i => (i, shared.Contains(i.Id)))], cancellationToken);
    }

    /// <summary>The leaf kinds of doc 10's Rollup.</summary>
    private static bool Leaf(string kind)
        => kind is "task" or "followup" or "subtask" or "spike" or "question" or "incident";
}
