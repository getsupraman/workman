using Microsoft.EntityFrameworkCore;
using Workman.Core.Board;
using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Core.Writing;
using Workman.Tests.Read;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// deadlines/3. A <c>wait</c> is waiting on something outside the system: it blocks the real task, its due is the chase
/// date, and it closes on a note of what arrived — no criteria, verification or commit. Every role may close one: an
/// orchestrator with <c>work_status(…, reason:)</c>, a worker with <c>work_report(…, status:"done", verify:)</c>.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class WaitKindTests(PostgresFixture fixture)
{
    private const string Arrived = "Client confirmed the budget by email, 2026-11-16";

    [Fact]
    public async Task WaitKind_is_seeded_with_no_close_requirements_and_is_creatable_under_a_run_a_backlog_and_a_task()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.SecondRunAsync(world, "backlog");

        await using (var db = fixture.NewContext())
        {
            var kind = await db.ItemKinds.SingleAsync(k => k.Key == "wait", Ct);
            Assert.False(kind.RequiresVerify);
            Assert.False(kind.RequiresCriteria);
            Assert.False(kind.RequiresCommit);
            Assert.False(kind.IsGateable);
        }

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
        [
            new NewItemArg("wait", "Under the run", Parent: "run-a"),
            new NewItemArg("wait", "Under the backlog", Ref: "client-budget", Parent: "backlog"),
            new NewItemArg("wait", "Under the task", Parent: "1.1"),
        ], Ct);

        Assert.False(created.Refused, created.Text);

        await using var check = fixture.NewContext();
        var waits = await check.Items
            .Where(i => i.WorkspaceId == world.WorkspaceId && i.Kind == "wait")
            .Select(i => i.Title)
            .ToListAsync(Ct);
        Assert.Equal(["Under the backlog", "Under the run", "Under the task"], waits.Order());
    }

    [Fact]
    public async Task WaitKind_close_without_a_reason_is_refused_naming_the_reason_argument()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var wait = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");

        var refused = await world.Writer.StatusAsync(
            world.WorkspaceId, "W1", "done", WriteSeed.Orchestrator, new StatusArgs(), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("work_status(ref:\"W1\", to:\"done\", reason:\"<what arrived>\")", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("W1"));

        await using var db = fixture.NewContext();
        Assert.False(await db.Notes.AnyAsync(n => n.FromItemId == wait, Ct));
    }

    [Fact]
    public async Task WaitKind_closes_from_open_with_a_reason_and_no_verification_and_journals_it_as_an_observation_note()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var wait = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, "W1", "done", WriteSeed.Orchestrator, new StatusArgs(Reason: Arrived), Ct);

        Assert.False(closed.Refused, closed.Text);
        Assert.Contains("observation note", closed.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("W1"));

        await using var db = fixture.NewContext();
        var note = await db.Notes.SingleAsync(n => n.FromItemId == wait, Ct);
        Assert.Equal(NoteKind.Observation, note.Kind);
        Assert.Equal(Arrived, note.Body);
        Assert.False(await db.Verifications.AnyAsync(v => v.ItemId == wait, Ct));
        Assert.Contains("status", await WriteSeed.EventKindsAsync(world, wait));
    }

    [Fact]
    public async Task WaitKind_reason_rule_leaves_a_task_close_unchanged()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);
        await WriteSeed.VerificationAsync(world, world.TaskId, VerifyRunner.Orchestrator);

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, new StatusArgs(Commit: "abc1234"), Ct);

        Assert.False(closed.Refused, closed.Text);

        await using var db = fixture.NewContext();
        Assert.False(await db.Notes.AnyAsync(n => n.FromItemId == world.TaskId, Ct));
    }

    [Fact]
    public async Task WaitKind_closing_a_wait_that_blocks_a_task_makes_the_task_ready()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var wait = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");
        await WriteSeed.LinkAsync(world, wait, world.TaskId, LinkType.Blocks);

        var queries = new WorkmanQueries(fixture.DataSource);
        Assert.DoesNotContain(await queries.ReadyFrontierAsync(world.RunId, Ct), r => r.Id == world.TaskId);

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, "W1", "done", WriteSeed.Orchestrator, new StatusArgs(Reason: Arrived), Ct);
        Assert.False(closed.Refused, closed.Text);

        Assert.Contains(await queries.ReadyFrontierAsync(world.RunId, Ct), r => r.Id == world.TaskId);
        var next = await world.Reader.NextAsync(await world.ScopeAsync("run-a"), 5, null, Ct);
        Assert.Contains("1.1", next, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitKind_an_open_wait_is_never_on_the_frontier_and_closing_it_brings_up_the_task_it_blocked()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var undated = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");
        var overdue = await WriteSeed.ChildAsync(world, world.PhaseId, "W2", kind: "wait", status: ItemStatus.Ready);
        await using (var db = fixture.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE item SET due_on = DATE '2020-01-01', priority = 100 WHERE id = {0};", [overdue], Ct);
        }

        await WriteSeed.LinkAsync(world, undated, world.TaskId, LinkType.Blocks);
        await WriteSeed.LinkAsync(world, overdue, world.TaskId, LinkType.Blocks);

        var queries = new WorkmanQueries(fixture.DataSource);
        var frontier = await queries.ReadyFrontierAsync(world.RunId, Ct);
        Assert.DoesNotContain(frontier, r => r.Kind == "wait");
        Assert.DoesNotContain(frontier, r => r.Id == world.TaskId);

        var next = await world.Reader.NextAsync(await world.ScopeAsync("run-a"), 10, null, Ct);
        Assert.DoesNotContain("W1", next, StringComparison.Ordinal);
        Assert.DoesNotContain("W2", next, StringComparison.Ordinal);

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("run-a"), "resume", Ct);
        Assert.DoesNotContain("work_status(ref:\"W1\", to:\"in_progress\")", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("work_status(ref:\"W2\", to:\"in_progress\")", brief, StringComparison.Ordinal);

        foreach (var wait in new[] { "W1", "W2" })
        {
            var closed = await world.Writer.StatusAsync(
                world.WorkspaceId, wait, "done", WriteSeed.Orchestrator, new StatusArgs(Reason: Arrived), Ct);
            Assert.False(closed.Refused, closed.Text);
        }

        frontier = await queries.ReadyFrontierAsync(world.RunId, Ct);
        Assert.Contains(frontier, r => r.Id == world.TaskId);
        Assert.DoesNotContain(frontier, r => r.Kind == "wait");
        Assert.Contains("1.1", await world.Reader.NextAsync(await world.ScopeAsync("run-a"), 10, null, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitKind_the_agenda_keeps_a_wait_under_overdue_and_due_by_but_never_under_actionable_since()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var overdue = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");
        var dueBy = await WriteSeed.ChildAsync(world, world.PhaseId, "W2", kind: "wait", status: ItemStatus.Ready);
        var task = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");

        await using (var db = fixture.NewContext())
        {
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE item SET opened_at = '2026-01-01T00:00:00Z' WHERE workspace_id = {0};", [world.WorkspaceId], Ct);
            await db.Database.ExecuteSqlRawAsync(
                "UPDATE item SET opened_at = '2026-11-19T10:00:00Z' WHERE id IN ({0}, {1}, {2});", [overdue, dueBy, task], Ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE item SET due_on = DATE '2026-11-19' WHERE id = {0};", [overdue], Ct);
            await db.Database.ExecuteSqlRawAsync("UPDATE item SET due_on = DATE '2026-11-22' WHERE id = {0};", [dueBy], Ct);
        }

        var reader = new BoardReader(
            new WorkmanQueries(fixture.DataSource), new BoardQueries(fixture.DataSource), new FixedClock(new DateTimeOffset(2026, 11, 20, 12, 0, 0, TimeSpan.Zero)));
        var agenda = await reader.AgendaOfAsync(
            await world.ScopeAsync("run-a"),
            DateValue.Parse("2026-11-22").Value!.Value,
            DateValue.Parse("2026-11-18T00:00:00Z").Value,
            Ct);

        Assert.Contains(agenda.Overdue, r => r.Ref == "W1");
        Assert.Contains(agenda.DueBy, r => r.Ref == "W2");
        Assert.Contains(agenda.Actionable, r => r.Ref == "1.2");
        Assert.DoesNotContain(agenda.Actionable, r => r.Ref is "W1" or "W2");
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task WaitKind_an_item_blocked_by_an_open_wait_shows_waiting_on_in_the_tree_and_its_worker_brief()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("wait", "Client confirms the budget email", Parent: "1", Due: "2026-11-20")], Ct);
        Assert.False(created.Refused, created.Text);

        var wait = await world.Fixture.NewContext().Items
            .SingleAsync(i => i.WorkspaceId == world.WorkspaceId && i.Kind == "wait", Ct);
        await WriteSeed.LinkAsync(world, wait.Id, world.TaskId, LinkType.Blocks);

        var line = $"waiting on {wait.Ref} Client confirms the budget email, due 2026-11-20";

        var tree = await world.Reader.TreeAsync(await world.ScopeAsync("run-a"), 9, [], true, Ct);
        var rows = tree.Split('\n');
        var task = Array.FindIndex(rows, r => r.TrimStart().StartsWith("1.1 ", StringComparison.Ordinal));
        Assert.True(task >= 0, tree);
        Assert.Equal(line, rows[task + 1].Trim());

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "worker", Ct);
        Assert.Contains($"  {line}", brief, StringComparison.Ordinal);

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, wait.Ref, "done", WriteSeed.Orchestrator, new StatusArgs(Reason: Arrived), Ct);
        Assert.False(closed.Refused, closed.Text);

        Assert.DoesNotContain("waiting on", await world.Reader.TreeAsync(await world.ScopeAsync("run-a"), 9, [], true, Ct), StringComparison.Ordinal);
        Assert.DoesNotContain("waiting on", await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "worker", Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitKind_a_worker_brief_on_the_wait_itself_names_the_report_that_closes_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("W1"), "worker", Ct);

        Assert.Contains("work_report(ref:\"W1\", status:\"done\", verify:\"<what arrived>\") closes it", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("closing is the orchestrator's call", brief, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitKind_a_worker_closes_a_wait_with_work_report_and_the_text_becomes_the_observation_note()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var wait = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");

        var closed = await world.Writer.ReportAsync(
            world.WorkspaceId, "W1", WriteSeed.Worker, new ReportArgs("done", Verify: Arrived), Ct);

        Assert.False(closed.Refused, closed.Text);
        Assert.Contains("→ done", closed.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("W1"));

        await using var db = fixture.NewContext();
        var note = await db.Notes.SingleAsync(n => n.FromItemId == wait, Ct);
        Assert.Equal(NoteKind.Observation, note.Kind);
        Assert.Equal(Arrived, note.Body);
        Assert.Equal("worker-1", note.CreatedBy);
        Assert.Equal(["reported", "status"], (await WriteSeed.EventKindsAsync(world, wait)).Where(k => k is "reported" or "status"));
    }

    [Fact]
    public async Task WaitKind_a_worker_report_on_a_wait_without_verify_is_refused_and_closes_nothing()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var wait = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");

        var refused = await world.Writer.ReportAsync(
            world.WorkspaceId, "W1", WriteSeed.Worker, new ReportArgs("done"), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("work_report(ref:\"W1\", status:\"done\", verify:\"<what arrived>\")", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("W1"));

        await using var db = fixture.NewContext();
        Assert.False(await db.Notes.AnyAsync(n => n.FromItemId == wait, Ct));
        Assert.False(await db.Attempts.AnyAsync(a => a.ItemId == wait, Ct));
    }

    [Fact]
    public async Task WaitKind_a_worker_report_done_on_a_task_still_closes_nothing()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.StatusAsync(world, world.TaskId, ItemStatus.InProgress);

        var reported = await world.Writer.ReportAsync(
            world.WorkspaceId, "1.1", WriteSeed.Worker, new ReportArgs("done", Verify: "dotnet test: Passed 12"), Ct);

        Assert.False(reported.Refused, reported.Text);
        Assert.Contains("Next: work_verify", reported.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));

        await using var db = fixture.NewContext();
        Assert.False(await db.Notes.AnyAsync(n => n.FromItemId == world.TaskId, Ct));
    }

    [Fact]
    public async Task WaitKind_a_worker_token_closes_a_wait_over_mcp_and_its_tool_list_is_unchanged()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var wait = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");
        await WriteSeed.LinkAsync(world, wait, world.TaskId, LinkType.Blocks);

        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var worker = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var tools = (await worker.ListToolsAsync(cancellationToken: Ct)).Select(t => t.Name).Where(n => n.StartsWith("work_", StringComparison.Ordinal));
        Assert.Equal(
            ["work_agenda", "work_brief", "work_get", "work_note", "work_report", "work_search", "work_verify"],
            tools.Order());

        var (text, refused) = await ReadServer.CallEitherAsync(
            worker,
            "work_report",
            new Dictionary<string, object?> { ["ref"] = "W1", ["status"] = "done", ["verify"] = Arrived },
            Ct);

        Assert.False(refused, text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("W1"));
        Assert.Contains(await new WorkmanQueries(fixture.DataSource).ReadyFrontierAsync(world.RunId, Ct), r => r.Id == world.TaskId);
    }
}
