using Workman.Core.Domain;
using Workman.Core.Rendering;
using Workman.Core.Writing;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// A run closed 8 of 10 units while every one of its audit findings stayed open: a finding held a
/// close only at blocker severity, and nothing said so. A task close now names the findings it leaves
/// open, and a run or phase does not close over an open finding anywhere in its subtree.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class FindingsCloseTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_task_closes_over_a_minor_finding_and_the_result_names_it_with_its_resolving_calls()
    {
        var world = await ReadyTaskAsync();
        await WriteSeed.ChildAsync(world, world.TaskId, "F-11", "finding", severity: "minor");

        var closed = await Status(world, "1.1", "done");

        Assert.False(closed.Refused, closed.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));
        Assert.Contains("findings  1 open under 1.1", closed.Text, StringComparison.Ordinal);
        Assert.Contains("F-11  child F-11", closed.Text, StringComparison.Ordinal);
        Assert.Contains("work_status(ref:\"F-11\", to:\"done\", commit:\"…\")", closed.Text, StringComparison.Ordinal);
        Assert.Contains(
            "work_status(ref:\"F-11\", to:\"deferred\", target:\"backlog\", reason:\"…\")",
            closed.Text,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_close_with_no_open_finding_prints_no_findings_line()
    {
        var world = await ReadyTaskAsync();
        await WriteSeed.ChildAsync(world, world.TaskId, "F-12", "finding", severity: "minor", status: ItemStatus.Done);

        var closed = await Status(world, "1.1", "done");

        Assert.False(closed.Refused, closed.Text);
        Assert.DoesNotContain("findings", closed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("F-12", closed.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_is_refused_over_a_minor_finding_on_itself_and_closes_once_it_is_deferred()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.RunId, "F-21", "finding", severity: "minor");
        await CloseRunChildrenAsync(world);

        var refused = await Status(world, "run-a", "done");

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("finding F-21 (minor)", refused.Text, StringComparison.Ordinal);
        Assert.Contains("work_status(ref:\"F-21\", to:\"done\", commit:", refused.Text, StringComparison.Ordinal);
        Assert.Contains("work_status(ref:\"F-21\", to:\"cancelled\", reason:", refused.Text, StringComparison.Ordinal);
        Assert.Contains(
            "work_status(ref:\"F-21\", to:\"deferred\", target:\"backlog\"", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Review, await world.StatusAsync("run-a"));

        var moved = await world.Writer.StatusAsync(
            world.WorkspaceId, "F-21", "deferred", WriteSeed.Orchestrator,
            new StatusArgs(Reason: "next run", Target: "backlog"), Ct);
        Assert.False(moved.Refused, moved.Text);

        var closed = await Status(world, "run-a", "done");

        Assert.False(closed.Refused, closed.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("run-a"));
    }

    [Fact]
    public async Task A_finding_two_levels_down_holds_the_phase_and_the_run_until_it_is_done()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var finding = await WriteSeed.ChildAsync(world, world.TaskId, "F-31", "finding", severity: "minor");
        await CloseRunChildrenAsync(world);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Review);

        var phase = await Status(world, "1", "done");

        Assert.True(phase.Refused, phase.Text);
        RefusalShape.SelfCorrecting(phase.Text);
        Assert.Contains("finding F-31 (minor)", phase.Text, StringComparison.Ordinal);

        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Done);

        var run = await Status(world, "run-a", "done");

        Assert.True(run.Refused, run.Text);
        RefusalShape.SelfCorrecting(run.Text);
        Assert.Contains("finding F-31 (minor)", run.Text, StringComparison.Ordinal);

        await WriteSeed.StatusAsync(world, finding, ItemStatus.Done);

        var closed = await Status(world, "run-a", "done");

        Assert.False(closed.Refused, closed.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("run-a"));
    }

    [Fact]
    public void Subtree_findings_past_the_listed_three_are_counted_and_each_listed_one_is_routed()
    {
        var unmet = DoneGuard.Evaluate(new DoneFacts(
            "run-a", "run", null, false, false, false, [], [], [], [],
            [
                new FindingFact("F-1", "a", "minor"),
                new FindingFact("F-2", "b", null),
                new FindingFact("F-3", "c", "major"),
                new FindingFact("F-4", "d", "minor"),
                new FindingFact("F-5", "e", "minor"),
            ]));

        Assert.Equal(4, unmet.Count);
        Assert.Contains("finding F-2 \"b\" is open under run-a", unmet[1].Problem, StringComparison.Ordinal);
        Assert.Contains("2 further findings", unmet[^1].Problem, StringComparison.Ordinal);
        RefusalShape.SelfCorrecting(Refusal.Transition("run-a", "done", unmet));
    }

    [Fact]
    public void The_close_notice_lists_three_findings_then_counts_the_rest()
    {
        var lines = DoneGuard.OpenFindingsNotice("1.1",
        [
            new BlockerFact("F-1", "a"),
            new BlockerFact("F-2", "b"),
            new BlockerFact("F-3", "c"),
            new BlockerFact("F-4", "d"),
        ]);

        var text = string.Join("\n", lines);

        Assert.Contains("F-3  c", text, StringComparison.Ordinal);
        Assert.DoesNotContain("F-4", text, StringComparison.Ordinal);
        Assert.Contains("and 1 more", text, StringComparison.Ordinal);
        Assert.Empty(DoneGuard.OpenFindingsNotice("1.1", []));
    }

    private async Task<WriteWorld> ReadyTaskAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);
        return world;
    }

    private static async Task CloseRunChildrenAsync(WriteWorld world)
    {
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.PhaseId, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, (await world.ItemAsync("2")).Id, ItemStatus.Done);
        await WriteSeed.StatusAsync(world, world.RunId, ItemStatus.Review);
    }

    private static Task<WriteResult> Status(WriteWorld world, string reference, string to)
        => world.Writer.StatusAsync(world.WorkspaceId, reference, to, WriteSeed.Orchestrator, new StatusArgs(), Ct);
}
