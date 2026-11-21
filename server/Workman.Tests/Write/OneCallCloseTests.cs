using Microsoft.EntityFrameworkCore;
using Workman.Core.Domain;
using Workman.Core.Writing;
using Workman.Tests.Read;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// The one-call close: <c>work_status(to:"done", verify:{…}, criteria:[…])</c> writes the evidence
/// and the move in one transaction, and the five conditions of doc 07 §3 are checked on the result.
/// Every refusal here is asserted to have written nothing — no verification, no tick, no event —
/// because a refused close that leaves half its evidence behind is a different item than it was.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class OneCallCloseTests(PostgresFixture fixture)
{
    private const string Cmd = "dotnet test --filter Guard";

    private static readonly VerifyArg Green = new(Cmd, "Passed! Failed: 0, Passed: 91", true);

    private static readonly CriterionArg[] BothTicked =
    [
        new("c1", Checked: true, Evidence: "Projector_replaces_facets passed"),
        new("c2", Checked: true, Evidence: "DeletesSkipped asserted in the log"),
    ];

    [Theory]
    [InlineData(ItemStatus.Open)]
    [InlineData(ItemStatus.Ready)]
    [InlineData(ItemStatus.InProgress)]
    public async Task OneCall_closes_from_open_ready_or_in_progress_with_the_evidence_in_the_same_call(ItemStatus from)
    {
        var world = await WorldAsync(from);

        var closed = await Close(world, new StatusArgs(Commit: "a1b2c3d", Verify: Green, Criteria: BothTicked));

        Assert.False(closed.Refused, closed.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));
        Assert.Contains("verify  ✓ by orchestrator", closed.Text, StringComparison.Ordinal);

        await using var db = fixture.NewContext();

        var verification = Assert.Single(db.Verifications.Where(v => v.ItemId == world.TaskId).ToList());
        Assert.Equal(VerifyRunner.Orchestrator, verification.RunBy);
        Assert.Equal(Cmd, verification.Command);
        Assert.True(verification.Passed);

        var criteria = db.ItemCriteria.Where(c => c.ItemId == world.TaskId).OrderBy(c => c.Seq).ToList();
        Assert.All(criteria, c => Assert.True(c.Checked));
        Assert.Equal("Projector_replaces_facets passed", criteria[0].Evidence);
        Assert.Equal("DeletesSkipped asserted in the log", criteria[1].Evidence);

        Assert.Contains(db.Artifacts.Where(a => a.ItemId == world.TaskId && a.Kind == "commit"), a => a.Value == "a1b2c3d");

        var kinds = await WriteSeed.EventKindsAsync(world, world.TaskId);
        Assert.Contains("updated", kinds);
        Assert.Contains("verified", kinds);
        Assert.Equal("status", kinds[^1]);
    }

    public static TheoryData<string> Unmet => ["unchecked", "red", "mismatch", "child", "blocker"];

    [Theory]
    [MemberData(nameof(Unmet))]
    public async Task OneCall_refused_for_any_unmet_condition_writes_nothing(string unmet)
    {
        var world = await WorldAsync(ItemStatus.Open);
        var args = new StatusArgs(Commit: "a1b2c3d", Verify: Green, Criteria: BothTicked);

        switch (unmet)
        {
            case "unchecked":
                args = args with { Criteria = [BothTicked[0]] };
                break;
            case "red":
                args = args with { Verify = Green with { ResultLine = "Failed! Failed: 3, Passed: 88", Passed = false } };
                break;
            case "mismatch":
                args = args with { Verify = Green with { Command = "dotnet test --filter Something.Else" } };
                break;
            case "child":
                await WriteSeed.ChildAsync(world, world.TaskId, "1.1a", "subtask");
                break;
            case "blocker":
                await WriteSeed.ChildAsync(world, world.TaskId, "F-07", "finding", severity: "blocker");
                break;
        }

        var before = await TraceAsync(world);

        var refused = await Close(world, args);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));
        Assert.Equal(before, await TraceAsync(world));
    }

    /// <summary>
    /// A red run or a different command in this call is not the evidence of this close, even when an
    /// older green run of the right command exists — the guard alone would have let both through.
    /// </summary>
    [Theory]
    [InlineData("red")]
    [InlineData("mismatch")]
    public async Task OneCall_refuses_its_own_bad_verification_even_over_an_older_green_one(string bad)
    {
        var world = await WorldAsync(ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator, Cmd);

        var verify = bad == "red"
            ? Green with { ResultLine = "Failed! Failed: 3, Passed: 88", Passed = false }
            : Green with { Command = "dotnet test --filter Something.Else" };

        var before = await TraceAsync(world);

        var refused = await Close(world, new StatusArgs(Commit: "a1b2c3d", Verify: verify, Criteria: BothTicked));

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains(bad == "red" ? "did not pass" : "Something.Else", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
        Assert.Equal(before, await TraceAsync(world));
    }

    [Fact]
    public async Task OneCall_refusal_lists_every_unmet_condition_at_once()
    {
        var world = await WorldAsync(ItemStatus.Open);
        await WriteSeed.ChildAsync(world, world.TaskId, "1.1a", "subtask");
        await WriteSeed.ChildAsync(world, world.TaskId, "F-07", "finding", severity: "blocker");

        var refused = await Close(world, new StatusArgs(
            Verify: Green with { Command = "dotnet test --filter Something.Else", Passed = false },
            Criteria: [BothTicked[0]]));

        Assert.True(refused.Refused, refused.Text);
        Assert.Contains("criterion c2", refused.Text, StringComparison.Ordinal);
        Assert.Contains("no verification with run_by=orchestrator", refused.Text, StringComparison.Ordinal);
        Assert.Contains("1.1a", refused.Text, StringComparison.Ordinal);
        Assert.Contains("F-07 (BLOCKER)", refused.Text, StringComparison.Ordinal);
        Assert.Contains("did not pass", refused.Text, StringComparison.Ordinal);
        Assert.Contains("Something.Else", refused.Text, StringComparison.Ordinal);
        RefusalShape.NamesTheOverride(refused.Text);
    }

    [Fact]
    public async Task OneCall_bundle_without_done_is_refused_and_writes_nothing()
    {
        var world = await WorldAsync(ItemStatus.Open);
        var before = await TraceAsync(world);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "in_progress", WriteSeed.Orchestrator, new StatusArgs(Verify: Green), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("only travel with to:\"done\"", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));
        Assert.Equal(before, await TraceAsync(world));
    }

    /// <summary>
    /// The item is otherwise closable — an orchestrator run exists and every criterion is ticked — so
    /// only the writer's own role check stands between a worker and the close.
    /// </summary>
    [Fact]
    public async Task OneCall_verify_from_a_worker_is_refused_at_the_writer()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator, Cmd);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "facets replaced", ticked: true);

        var before = await TraceAsync(world);

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Worker, new StatusArgs(Commit: "a1b2c3d", Verify: Green), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("a worker cannot close", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
        Assert.Equal(before, await TraceAsync(world));
    }

    /// <summary>The widening is for the bundle alone: the same otherwise-closable item, from open, without it, is refused.</summary>
    [Fact]
    public async Task OneCall_done_from_open_without_the_bundle_stays_refused_and_names_the_one_call_form()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator, Cmd);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "facets replaced", ticked: true);

        var refused = await Close(world, new StatusArgs(Commit: "a1b2c3d"));

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("not reachable from open", refused.Text, StringComparison.Ordinal);
        Assert.Contains("verify:{command:", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));
    }

    [Fact]
    public async Task OneCall_incomplete_verify_or_unknown_criterion_is_refused_and_writes_nothing()
    {
        var world = await WorldAsync(ItemStatus.Open);
        var before = await TraceAsync(world);

        var incomplete = await Close(world, new StatusArgs(Verify: new VerifyArg(Command: Cmd)));
        var unknown = await Close(world, new StatusArgs(
            Verify: Green, Criteria: [.. BothTicked, new CriterionArg("c9", Checked: true, Evidence: "no such")]));

        Assert.True(incomplete.Refused, incomplete.Text);
        RefusalShape.SelfCorrecting(incomplete.Text);
        Assert.Contains("verify.result_line is missing", incomplete.Text, StringComparison.Ordinal);
        Assert.Contains("verify.passed is missing", incomplete.Text, StringComparison.Ordinal);

        Assert.True(unknown.Refused, unknown.Text);
        RefusalShape.SelfCorrecting(unknown.Text);
        Assert.Contains("work_status(1.1)", unknown.Text, StringComparison.Ordinal);
        Assert.Contains("\"c9\" does not exist", unknown.Text, StringComparison.Ordinal);

        Assert.Equal(before, await TraceAsync(world));
    }

    /// <summary>With <c>force</c>, the bundle is still written, and what it failed is named in the forced_transition.</summary>
    [Fact]
    public async Task OneCall_with_force_writes_the_bundle_then_forces_over_what_is_unmet()
    {
        var world = await WorldAsync(ItemStatus.InProgress);
        var red = Green with { ResultLine = "Failed! Failed: 3, Passed: 88", Passed = false };

        var forced = await Close(world, new StatusArgs(
            Reason: "the outage is upstream", Commit: "a1b2c3d", Force: true, Verify: red, Criteria: BothTicked));

        Assert.False(forced.Refused, forced.Text);
        Assert.Contains("FORCED", forced.Text, StringComparison.Ordinal);
        Assert.Contains("did not pass", forced.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));

        await using var db = fixture.NewContext();
        Assert.False(Assert.Single(db.Verifications.Where(v => v.ItemId == world.TaskId).ToList()).Passed);
        Assert.Contains("forced_transition", await WriteSeed.EventKindsAsync(world, world.TaskId));
    }

    /// <summary>The bundle as a client sends it: snake_case JSON through the MCP transport, with an orchestrator token.</summary>
    [Fact]
    public async Task OneCall_closes_through_the_transport()
    {
        var world = await WorldAsync(ItemStatus.Open);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);

        var (text, refused) = await ReadServer.CallEitherAsync(
            client,
            "work_status",
            new Dictionary<string, object?>
            {
                ["ref"] = "1.1",
                ["to"] = "done",
                ["commit"] = "a1b2c3d",
                ["verify"] = new Dictionary<string, object?>
                {
                    ["command"] = Cmd,
                    ["result_line"] = "Passed! Failed: 0, Passed: 91",
                    ["passed"] = true,
                    ["red_check"] = "reverted the projector: 4 failed",
                },
                ["criteria"] = new object[]
                {
                    new Dictionary<string, object?> { ["id"] = "c1", ["checked"] = true, ["evidence"] = "asserted" },
                    new Dictionary<string, object?> { ["id"] = "c2", ["checked"] = true, ["evidence"] = "asserted" },
                },
            },
            Ct);

        Assert.False(refused, text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));

        await using var db = fixture.NewContext();
        var verification = Assert.Single(db.Verifications.Where(v => v.ItemId == world.TaskId).ToList());
        Assert.Equal(VerifyRunner.Orchestrator, verification.RunBy);
        Assert.Contains("reverted the projector", verification.RedCheck ?? string.Empty, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ItemStatus.Open)]
    [InlineData(ItemStatus.Ready)]
    public void OneCall_legality_widens_open_and_ready_for_the_bundle_only(ItemStatus from)
    {
        Assert.True(Transitions.Legal(from, Move.Done, "task", started: false, bundled: true));
        Assert.False(Transitions.Legal(from, Move.Done, "task", started: false, bundled: false));
    }

    [Theory]
    [InlineData(ItemStatus.Blocked)]
    [InlineData(ItemStatus.Done)]
    [InlineData(ItemStatus.Cancelled)]
    public void OneCall_bundle_does_not_open_done_from_anywhere_else(ItemStatus from)
        => Assert.False(Transitions.Legal(from, Move.Done, "task", started: true, bundled: true));

    /// <summary>A task with a verification command and two unchecked criteria, in <paramref name="status"/>, never started unless in progress.</summary>
    private async Task<WriteWorld> WorldAsync(ItemStatus status)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "facets replaced per entity", ticked: false);
        await WriteSeed.CriterionAsync(world, world.TaskId, 2, "DeletesSkipped surfaced", ticked: false);

        await using var db = fixture.NewContext();
        var item = await db.Items.FirstAsync(i => i.Id == world.TaskId, Ct);
        item.Status = status;
        item.StartedAt = status == ItemStatus.InProgress ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync(Ct);

        return world;
    }

    private static Task<WriteResult> Close(WriteWorld world, StatusArgs args)
        => world.Writer.StatusAsync(world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, args, Ct);

    /// <summary>Everything a close can write on the item, as one comparable value.</summary>
    private async Task<string> TraceAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();

        var verifications = await db.Verifications.CountAsync(v => v.ItemId == world.TaskId, Ct);
        var ticks = await db.ItemCriteria
            .Where(c => c.ItemId == world.TaskId)
            .OrderBy(c => c.Seq)
            .Select(c => $"c{c.Seq}:{c.Checked}:{c.Evidence}")
            .ToListAsync(Ct);
        var events = await db.Events.CountAsync(e => e.ItemId == world.TaskId, Ct);
        var artifacts = await db.Artifacts.CountAsync(a => a.ItemId == world.TaskId, Ct);
        var item = await db.Items.FirstAsync(i => i.Id == world.TaskId, Ct);

        return $"v={verifications} t={string.Join(",", ticks)} e={events} a={artifacts} s={item.Status} r={item.Revision}";
    }
}
