using Dapper;
using Workman.Core.Board;
using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Core.Writing;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Read;

/// <summary>
/// deadlines/4: work_agenda's three lists, and work_next leaving out what is not yet actionable and ranking overdue,
/// then due today, ahead of priority. "Now" is the injected clock (C242); every item is opened on 2026-01-01 so the
/// real clock never decides what became actionable.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class AgendaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Agenda_overdue_and_due_by_read_a_day_in_the_workspace_zone_and_name_open_blockers()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await ZoneAsync(world, "Europe/Rome");
        await DueAsync(world, world.TaskId, "2026-11-20");
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.2"), "2026-11-22");
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.3"), "2026-11-23");
        var closed = await WriteSeed.ChildAsync(world, world.PhaseId, "1.4", status: ItemStatus.Done);
        await ExecAsync(world, "UPDATE item SET due_at = '2026-11-19T10:00:00Z' WHERE id = @id;", new { id = closed });
        var blocked = await WriteSeed.ChildAsync(world, world.PhaseId, "1.5");
        await DueAsync(world, blocked, "2026-11-19");
        var blocker = await WriteSeed.ChildAsync(world, world.PhaseId, "1.6");
        await WriteSeed.LinkAsync(world, blocker, blocked, LinkType.Blocks);
        await OpenedAsync(world);

        // 23:30 UTC on the 20th is already the 21st in Rome: the 20th is over there.
        var late = await AgendaAsync(world, At(2026, 11, 20, 23, 30), "2026-11-22");
        Assert.Equal(["1.5", "1.1"], late.Overdue.Select(r => r.Ref));
        Assert.Equal(["1.2"], late.DueBy.Select(r => r.Ref));
        Assert.Equal(["1.6"], Assert.Single(late.Overdue, r => r.Ref == "1.5").Blockers);

        var evening = await AgendaAsync(world, At(2026, 11, 20, 22, 30), "2026-11-22");
        Assert.Equal(["1.5"], evening.Overdue.Select(r => r.Ref));
        Assert.Equal(["1.1", "1.2"], evening.DueBy.Select(r => r.Ref));

        // An instant until is an instant: 22:00 UTC on the 22nd is before the 22nd ends in Rome.
        var instant = await AgendaAsync(world, At(2026, 11, 20, 22, 30), "2026-11-22T22:00:00Z");
        Assert.Equal(["1.1"], instant.DueBy.Select(r => r.Ref));

        var text = await TextAsync(world, At(2026, 11, 20, 23, 30), "2026-11-22", null);
        Assert.Contains("OVERDUE (2)", text, StringComparison.Ordinal);
        Assert.Contains("  1.5  child 1.5  ·  due 2026-11-19  ·  open  ·  blocked by 1.6", text, StringComparison.Ordinal);
        Assert.Contains("DUE BY 2026-11-22 (1)", text, StringComparison.Ordinal);
        Assert.Contains("zone Europe/Rome", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.3  ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("1.4  ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Agenda_actionable_since_counts_a_blocker_closed_and_a_not_before_passed_after_since()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var freedLate = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);
        await WriteSeed.LinkAsync(world, freedLate, world.TaskId, LinkType.Blocks);
        var freedEarly = await WriteSeed.ChildAsync(world, world.PhaseId, "1.3");
        var earlyBlocker = await WriteSeed.ChildAsync(world, world.PhaseId, "1.4", status: ItemStatus.Done);
        await WriteSeed.LinkAsync(world, earlyBlocker, freedEarly, LinkType.Blocks);
        var passed = await WriteSeed.ChildAsync(world, world.PhaseId, "1.5");
        var future = await WriteSeed.ChildAsync(world, world.PhaseId, "1.6");
        var fresh = await WriteSeed.ChildAsync(world, world.PhaseId, "1.7");
        await OpenedAsync(world);

        await ExecAsync(world, "UPDATE item SET closed_at = '2026-11-19T08:00:00Z' WHERE id = @id;", new { id = freedLate });
        await ExecAsync(world, "UPDATE item SET closed_at = '2026-11-17T08:00:00Z' WHERE id = @id;", new { id = earlyBlocker });
        await ExecAsync(world, "UPDATE item SET not_before_on = '2026-11-19' WHERE id = @id;", new { id = passed });
        await ExecAsync(world, "UPDATE item SET not_before_on = '2026-11-21' WHERE id = @id;", new { id = future });
        await ExecAsync(world, "UPDATE item SET opened_at = '2026-11-19T10:00:00Z' WHERE id = @id;", new { id = fresh });

        var agenda = await AgendaAsync(world, At(2026, 11, 20, 12, 0), "2026-11-20", "2026-11-18T00:00:00Z");
        Assert.Equal(["1.5", "1.1", "1.7"], agenda.Actionable.Select(r => r.Ref));

        // No since: the start of today in the zone (UTC here), so only what became ready on the 20th.
        await ExecAsync(world, "UPDATE item SET not_before_on = '2026-11-20' WHERE id = @id;", new { id = passed });
        var today = await AgendaAsync(world, At(2026, 11, 20, 12, 0), "2026-11-20", null);
        Assert.Equal(["1.5"], today.Actionable.Select(r => r.Ref));

        var text = await TextAsync(world, At(2026, 11, 20, 12, 0), "2026-11-20", "2026-11-18");
        Assert.Contains("ACTIONABLE SINCE 2026-11-18 (3)", text, StringComparison.Ordinal);
        Assert.Contains("OVERDUE (0)\n  none", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Next_leaves_out_an_item_before_its_not_before_and_ranks_overdue_then_due_today_ahead_of_priority()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await ExecAsync(world, "UPDATE item SET not_before_on = '2026-11-21' WHERE id = @id;", new { id = world.TaskId });
        var urgent = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await ExecAsync(world, "UPDATE item SET priority = 5 WHERE id = @id;", new { id = urgent });
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.3"), "2026-11-19");
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.4"), "2026-11-20");
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.5"), "2026-11-30");

        var queries = new WorkmanQueries(fixture.DataSource);
        var noon = await queries.ReadyFrontierAsync(world.RunId, At(2026, 11, 20, 12, 0), Ct);
        Assert.Equal(["1.3", "1.4", "1.2", "1.5", "2"], noon.Select(r => r.Ref));

        var next = await Reader(world, At(2026, 11, 20, 12, 0)).NextAsync(await world.ScopeAsync("run-a"), 10, null, Ct);
        Assert.DoesNotContain("The task under test", next, StringComparison.Ordinal);

        var reached = await queries.ReadyFrontierAsync(world.RunId, At(2026, 11, 21, 0, 0), Ct);
        Assert.Contains("1.1", reached.Select(r => r.Ref));
    }

    [Fact]
    public async Task work_agenda_reaches_a_worker_and_refuses_a_date_it_cannot_read()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var client = await server.ConnectAsync(ReadServer.WorkerToken, Ct);

        var text = await ReadServer.CallAsync(client, "work_agenda",
            new Dictionary<string, object?> { ["until"] = "2099-01-01", ["scope"] = "run-a" }, Ct);
        Assert.StartsWith("AGENDA  run-a", text, StringComparison.Ordinal);
        Assert.Contains("DUE BY 2099-01-01 (0)", text, StringComparison.Ordinal);

        var refused = (await ReadServer.CallEitherAsync(client, "work_agenda",
            new Dictionary<string, object?> { ["until"] = "next friday" }, Ct)).Text;
        Assert.Contains("neither a date (YYYY-MM-DD) nor an ISO date-time", refused, StringComparison.Ordinal);
        Assert.Contains("work_agenda(until:\"2026-11-20\")", refused, StringComparison.Ordinal);
    }

    private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0)
        => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static BoardReader Reader(WriteWorld world, DateTimeOffset now)
        => new(new WorkmanQueries(world.Fixture.DataSource), new BoardQueries(world.Fixture.DataSource), new FixedClock(now));

    private static async Task<Agenda> AgendaAsync(WriteWorld world, DateTimeOffset now, string until, string? since = null)
        => await Reader(world, now).AgendaOfAsync(
            await world.ScopeAsync("run-a"),
            DateValue.Parse(until).Value!.Value,
            since is null ? null : DateValue.Parse(since).Value,
            Ct);

    private static async Task<string> TextAsync(WriteWorld world, DateTimeOffset now, string until, string? since)
        => await Reader(world, now).AgendaAsync(
            await world.ScopeAsync("run-a"),
            DateValue.Parse(until).Value!.Value,
            since is null ? null : DateValue.Parse(since).Value,
            Ct);

    private static Task DueAsync(WriteWorld world, Guid itemId, string day)
        => ExecAsync(world, "UPDATE item SET due_on = @day::date WHERE id = @id;", new { id = itemId, day });

    /// <summary>Every item of the world opened long before any date here.</summary>
    private static Task OpenedAsync(WriteWorld world)
        => ExecAsync(world, "UPDATE item SET opened_at = '2026-01-01T00:00:00Z' WHERE workspace_id = @id;", new { id = world.WorkspaceId });

    private static Task ZoneAsync(WriteWorld world, string zone)
        => ExecAsync(world,
            "UPDATE workspace SET settings = jsonb_set(settings, '{timezone}', to_jsonb(@zone::text)) WHERE id = @id;",
            new { id = world.WorkspaceId, zone });

    private static async Task ExecAsync(WriteWorld world, string sql, object parameters)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(sql, parameters);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
