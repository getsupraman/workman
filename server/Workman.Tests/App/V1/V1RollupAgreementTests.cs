using Microsoft.EntityFrameworkCore;
using Workman.Core.Domain;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.App.V1.V1Http;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary>
/// One run, one "X of Y" (rollup-two-counts). <c>work_tree</c> counted every descendant, phases with
/// children included, and <c>work_brief</c> counted tasks and sub-tasks only, while the dashboard
/// and <c>/api/v1</c> count leaves plus childless phases and features — so a human reading the
/// board beside the tree saw three different numbers for the same run.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1RollupAgreementTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Work_tree_work_brief_and_the_api_print_the_same_done_and_total_for_one_run()
    {
        // run-a: phase 1 (1.1 open, 1.2 done), phase 2 empty, task 3 directly under the run.
        // Counted: 1.1, 1.2, phase 2, task 3 — 1 of 4. Every descendant would be 1 of 5; tasks only 1 of 3.
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);
        await WriteSeed.ChildAsync(world, world.RunId, "3");

        await using var server = await StartAsync(fixture, world.Slug);
        var reader = Reader(server);

        var rows = Array(await GetAsync(reader, $"/api/v1/runs/{world.RunId}/board"), "rows");
        var (runDone, runTotal) = Counted(ByRef(rows, "run-a").GetProperty("rollup"));
        var (phaseDone, phaseTotal) = Counted(ByRef(rows, "1").GetProperty("rollup"));
        Assert.Equal((1, 4), (runDone, runTotal));
        Assert.Equal((1, 2), (phaseDone, phaseTotal));

        var overview = Assert.Single(Array(await GetAsync(reader, "/api/v1/overview"), "projects"));
        Assert.Equal((runDone, runTotal), Counted(Assert.Single(Array(overview, "runs")).GetProperty("rollup")));
        Assert.Equal((runDone, runTotal), Counted(overview.GetProperty("rollup")));

        var tree = (await world.Reader.TreeAsync(await world.ScopeAsync("run-a"), 3, [], true, Ct)).Split('\n');
        Assert.Contains($"{runDone}/{runTotal} done", Row(tree, "run-a"), StringComparison.Ordinal);
        Assert.Contains($"{phaseDone}/{phaseTotal} done", Row(tree, "1"), StringComparison.Ordinal);

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("run-a"), "resume", Ct);
        Assert.Contains($"{runDone} of {runTotal} done across the run.", brief, StringComparison.Ordinal);

        var gate = await world.Reader.BriefAsync(await world.ScopeAsync("1"), "gate", Ct);
        Assert.Contains($"·  {phaseDone} of {phaseTotal} done", gate.Split('\n')[0], StringComparison.Ordinal);

        string projectRef;
        await using (var db = fixture.NewContext())
        {
            projectRef = (await db.Items.SingleAsync(i => i.Id == world.ProjectId, Ct)).Ref;
        }

        var projectBrief = (await world.Reader.BriefAsync(await world.ScopeAsync(projectRef), "resume", Ct)).Split('\n');
        var runLine = System.Array.IndexOf(projectBrief, "  run-a — The run");
        Assert.True(runLine > 0, string.Join('\n', projectBrief));
        Assert.StartsWith($"    {runDone}/{runTotal} done", projectBrief[runLine + 1], StringComparison.Ordinal);
    }

    /// <summary>The dashboard's <c>counted()</c> in <c>state.ts</c>: cancelled and superseded are outside the total.</summary>
    private static (int Done, int Total) Counted(System.Text.Json.JsonElement rollup)
        => (rollup.GetProperty("done").GetInt32(),
            rollup.GetProperty("total").GetInt32()
            - rollup.GetProperty("cancelled").GetInt32()
            - rollup.GetProperty("superseded").GetInt32());

    private static string Row(string[] tree, string reference)
        => tree.Single(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(reference));
}
