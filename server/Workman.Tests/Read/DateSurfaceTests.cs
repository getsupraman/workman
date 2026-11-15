using System.Text.Json;
using Dapper;
using Workman.Core.Board;
using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Core.Rendering;
using Workman.Tests.App;
using Workman.Tests.App.V1;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Read;

/// <summary>
/// deadlines/5: dates where an agent already looks — the brief's DATES block, attention, the inbox and the plan
/// audit — and an assumption whose revisit_at has passed. "Now" is the injected clock (C242).
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class DateSurfaceTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Noon = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Brief_of_a_scope_with_nothing_due_soon_has_no_dates_block_and_is_unchanged()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var closed = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);
        var scope = await world.ScopeAsync("run-a");

        var undated = await Reader(world, Noon).BriefAsync(scope, "resume", Ct);
        Assert.DoesNotContain("DATES", undated, StringComparison.Ordinal);
        Assert.DoesNotContain("DATES", await Reader(world, Noon).BriefAsync(scope, "worker", Ct), StringComparison.Ordinal);

        // A due outside the window, and one on a closed item, leave the brief byte-for-byte as it was.
        await DueAsync(world, world.TaskId, "2026-12-24");
        await DueAsync(world, closed, "2026-11-01");

        Assert.Equal(undated, await Reader(world, Noon).BriefAsync(scope, "resume", Ct));
    }

    [Fact]
    public async Task Brief_dates_block_lists_overdue_today_and_the_next_three_days_capped_at_five_with_a_work_agenda_pointer()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await ZoneAsync(world, "Europe/Rome");
        await DueAsync(world, world.TaskId, "2026-11-19");
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.2"), "2026-11-20");
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.3"), "2026-11-21");
        var blocked = await WriteSeed.ChildAsync(world, world.PhaseId, "1.4");
        await DueAsync(world, blocked, "2026-11-22");
        await WriteSeed.LinkAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.8"), blocked, LinkType.Blocks);
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.5"), "2026-11-23");
        await ExecAsync(world, "UPDATE item SET due_at = '2026-11-23T10:00:00Z' WHERE id = @id;",
            new { id = await WriteSeed.ChildAsync(world, world.PhaseId, "1.6") });
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.7"), "2026-11-24");

        var brief = await Reader(world, Noon).BriefAsync(await world.ScopeAsync("run-a"), "resume", Ct);

        var expected = string.Join("\n",
            "DATES (6)   — zone Europe/Rome, today 2026-11-20",
            "  overdue    1.1  The task under test  ·  due 2026-11-19",
            "  today      1.2  child 1.2  ·  due 2026-11-20",
            "  tomorrow   1.3  child 1.3  ·  due 2026-11-21",
            "  in 2 days  1.4  child 1.4  ·  due 2026-11-22  ·  blocked by 1.8",
            "  in 3 days  1.6  child 1.6  ·  due 2026-11-23T10:00:00Z",
            "  · +1 more — work_agenda(scope:\"run-a\", until:\"2026-11-23\")");
        Assert.Contains("\n\n" + expected + "\n", brief, StringComparison.Ordinal);
        Assert.DoesNotContain("1.7  child 1.7", brief, StringComparison.Ordinal);

        // The block sits between NEXT and the next section.
        Assert.True(brief.IndexOf("NEXT   ", StringComparison.Ordinal) < brief.IndexOf("DATES (6)", StringComparison.Ordinal));

        // The project brief prints the same block.
        var project = await Reader(world, Noon).BriefAsync(await world.ScopeAsync((await ProjectRefAsync(world))), "resume", Ct);
        Assert.Contains("DATES (6)   — zone Europe/Rome, today 2026-11-20", project, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Attention_names_overdue_items_and_items_due_within_three_days_still_blocked()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await DueAsync(world, world.TaskId, "2026-11-19");
        var blocked = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await DueAsync(world, blocked, "2026-11-22");
        await WriteSeed.LinkAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.3"), blocked, LinkType.Blocks);
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.4"), "2026-11-22");
        var later = await WriteSeed.ChildAsync(world, world.PhaseId, "1.5");
        await DueAsync(world, later, "2026-11-30");
        await WriteSeed.LinkAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.6"), later, LinkType.Blocks);
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.7", status: ItemStatus.Done), "2026-11-01");

        var lines = await Reader(world, Noon).AttentionAsync(await world.ScopeAsync("run-a"), Ct);

        var overdue = Assert.Single(lines, l => l.Rank == AttentionRank.Overdue);
        Assert.Equal("overdue: 1.1 The task under test (due 2026-11-19)", overdue.What);
        Assert.Equal("work_get(refs:[\"1.1\"]), then finish it or work_update(ref:\"1.1\", due:\"…\")", overdue.Call);
        Assert.Equal("run-a/1.1", overdue.QualifiedRef);

        var soon = Assert.Single(lines, l => l.Rank == AttentionRank.DueSoonBlocked);
        Assert.Equal("1.2 due in 2 days, still blocked by 1.3", soon.What);
        Assert.Equal("work_get(refs:[\"1.3\"]) to unblock it", soon.Call);

        // The day before, 1.1 is due tomorrow and unblocked: no line at all.
        var before = await Reader(world, Noon.AddDays(-2)).AttentionAsync(await world.ScopeAsync("run-a"), Ct);
        Assert.DoesNotContain(before, l => l.Rank == AttentionRank.Overdue);

        // Ranked after the closing obligations and ahead of the softer ones, which keep their order.
        Assert.True(AttentionRank.DoneUnverified < AttentionRank.Overdue);
        Assert.True(AttentionRank.DueSoonBlocked < AttentionRank.AssumptionPastRevisit);
    }

    [Fact]
    public async Task An_assumption_past_its_revisit_at_is_due_once_even_when_its_revisit_item_closed_too()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var opened = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "run-a/1.1",
            "the vendor export is reachable", "reasoned", "medium", null, null, null, Ct);
        Assert.False(opened.Refused, opened.Text);
        await ExecAsync(world, "UPDATE assumption SET revisit_at = '2026-11-19T09:00:00Z' WHERE workspace_id = @id;",
            new { id = world.WorkspaceId });
        var scope = await world.ScopeAsync("run-a");

        var due = Assert.Single(
            await Reader(world, Noon).AttentionAsync(scope, Ct),
            l => l.Rank == AttentionRank.AssumptionPastRevisit);
        Assert.Equal("A1 is past revisit_at (2026-11-19 09:00 UTC)", due.What);

        Assert.DoesNotContain(
            await Reader(world, new DateTimeOffset(2026, 11, 19, 8, 0, 0, TimeSpan.Zero)).AttentionAsync(scope, Ct),
            l => l.Rank == AttentionRank.AssumptionPastRevisit);

        // Both rules hold: still one line, the revisit item's.
        var revisit = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);
        await ExecAsync(world, "UPDATE assumption SET revisit_item_id = @revisit WHERE workspace_id = @id;",
            new { id = world.WorkspaceId, revisit });
        var both = Assert.Single(
            await Reader(world, Noon).AttentionAsync(scope, Ct),
            l => l.Rank == AttentionRank.AssumptionPastRevisit);
        Assert.Equal("A1 is past revisit_when (1.2 is closed)", both.What);
    }

    [Fact]
    public async Task Audit_flags_an_open_child_whose_due_ends_after_its_parents_reading_both_in_the_zone()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await DueAsync(world, world.PhaseId, "2026-11-20");
        await ExecAsync(world, "UPDATE item SET due_at = '2026-11-20T23:30:00Z' WHERE id = @id;", new { id = world.TaskId });
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.2"), "2026-11-21");
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.3"), "2026-11-20");
        await DueAsync(world, await WriteSeed.ChildAsync(world, world.PhaseId, "1.4", status: ItemStatus.Done), "2026-11-25");
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.5");
        var scope = await world.ScopeAsync("run-a");

        // UTC: the 20th ends at midnight, so 23:30 on the 20th is inside it.
        var utc = await Reader(world, Noon).AuditAsync(scope, Ct);
        Assert.Contains("due-after-parent (1)", utc, StringComparison.Ordinal);
        Assert.Contains("1.2", Section(utc), StringComparison.Ordinal);
        Assert.Contains("due 2026-11-21 ends after its parent 1 is due (2026-11-20)", utc, StringComparison.Ordinal);
        Assert.Contains("work_update(ref:\"1.2\", due:\"…\")", utc, StringComparison.Ordinal);

        // Rome: the 20th ends at 23:00 UTC, so the instant is after it.
        await ZoneAsync(world, "Europe/Rome");
        var rome = await Reader(world, Noon).AuditAsync(scope, Ct);
        Assert.Contains("due-after-parent (2)", rome, StringComparison.Ordinal);
        Assert.Contains("1.1", Section(rome), StringComparison.Ordinal);
        Assert.DoesNotContain("1.3", Section(rome), StringComparison.Ordinal);
        Assert.DoesNotContain("1.4", Section(rome), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Inbox_has_one_human_overdue_row_per_open_overdue_item_and_an_assumption_due_by_revisit_at()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await DueAsync(world, world.TaskId, "2020-01-15");
        var future = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await DueAsync(world, future, "2099-01-01");
        var closed = await WriteSeed.ChildAsync(world, world.PhaseId, "1.3", status: ItemStatus.Done);
        await DueAsync(world, closed, "2020-01-01");
        var opened = await world.Writer.AssumeAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "run-a/1.1",
            "the vendor export is reachable", "reasoned", "medium", null, null, null, Ct);
        Assert.False(opened.Refused, opened.Text);
        await ExecAsync(world, "UPDATE assumption SET revisit_at = '2020-01-10T09:00:00Z' WHERE workspace_id = @id;",
            new { id = world.WorkspaceId });

        await using var server = await V1Http.StartAsync(fixture, world.Slug);
        var rows = V1Http.Array(
            await V1Http.GetAsync(await V1Http.ReaderAsync(server), $"/api/v1/inbox?project={world.ProjectId}"),
            "rows");

        var overdue = Assert.Single(rows, r => r.GetProperty("kind").GetString() == "overdue");
        Assert.Equal(world.TaskId, overdue.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal("human", overdue.GetProperty("audience").GetString());
        Assert.Equal("due 2020-01-15", overdue.GetProperty("detail").GetString());
        Assert.Equal($"overdue:{world.TaskId}", overdue.GetProperty("key").GetString());
        Assert.Equal(new DateTimeOffset(2020, 1, 16, 0, 0, 0, TimeSpan.Zero), overdue.GetProperty("since").GetDateTimeOffset());

        var assumption = Assert.Single(rows, r => r.GetProperty("kind").GetString() == "assumption_due");
        Assert.Equal(world.TaskId, assumption.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal("controller", assumption.GetProperty("audience").GetString());
        Assert.Equal(1, assumption.GetProperty("count").GetInt32());
        Assert.Equal(new DateTimeOffset(2020, 1, 10, 9, 0, 0, TimeSpan.Zero), assumption.GetProperty("since").GetDateTimeOffset());

        // The overview counts the same rows.
        var overview = await V1Http.GetAsync(await V1Http.ReaderAsync(server), "/api/v1/overview");
        Assert.True(overview.GetProperty("inboxCount").GetInt32() >= 2);
        Assert.Equal(JsonValueKind.Object, overview.ValueKind);
    }

    /// <summary>The due-after-parent group: the indented rows under its header.</summary>
    private static string Section(string audit)
        => string.Join("\n", audit.Split('\n')
            .SkipWhile(l => !l.StartsWith("due-after-parent", StringComparison.Ordinal))
            .Skip(1)
            .TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal)));

    private static BoardReader Reader(WriteWorld world, DateTimeOffset now)
        => new(new WorkmanQueries(world.Fixture.DataSource), new BoardQueries(world.Fixture.DataSource), new FixedClock(now));

    private static async Task<string> ProjectRefAsync(WriteWorld world)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<string>("SELECT ref FROM item WHERE id = @id;", new { id = world.ProjectId })
               ?? throw new InvalidOperationException("no project");
    }

    private static Task DueAsync(WriteWorld world, Guid itemId, string day)
        => ExecAsync(world, "UPDATE item SET due_on = @day::date WHERE id = @id;", new { id = itemId, day });

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
