using System.Text.Json;
using Dapper;
using Workman.Core.Board;
using Workman.Core.Queries;
using Workman.Core.Writing;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// deadlines/2: due and not_before on work_create and work_update. A day is stored as a day and read in the workspace's
/// time zone at read time (C242), every change is journalled as dates_changed {field, from, to}, and work_get prints the
/// dates, the slips (due moved later) and a late close. "Now" is the injected clock, never the system's.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class DateTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Dates_work_create_stores_a_day_and_an_instant_and_the_created_event_carries_them()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", "Dated", Parent: "1", VerifyCmd: "dotnet test", Criteria: ["it holds"],
                Due: "2026-11-20", NotBefore: "2026-11-18T09:30:00+01:00")], Ct);

        Assert.False(created.Refused, created.Text);
        var item = await world.ItemAsync("1.2");
        Assert.Equal<(string?, string?, string?, string?)>(("2026-11-20", null, null, "2026-11-18 08:30"), await ColumnsAsync(world, item.Id));

        var payload = await PayloadAsync(world, item.Id, "created");
        Assert.Equal("2026-11-20", payload.GetProperty("due").GetString());
        Assert.Equal("2026-11-18T08:30:00Z", payload.GetProperty("not_before").GetString());
        Assert.Empty(await PayloadsAsync(world, item.Id, "dates_changed"));
    }

    [Fact]
    public async Task Dates_an_undated_create_journals_the_created_event_exactly_as_before()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var created = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", "Undated", Parent: "1", VerifyCmd: "dotnet test", Criteria: ["it holds"])], Ct);

        Assert.False(created.Refused, created.Text);
        var item = await world.ItemAsync("1.2");
        var payload = await PayloadAsync(world, item.Id, "created");
        Assert.Equal(new[] { "kind", "parent", "title" }, payload.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal<(string?, string?, string?, string?)>((null, null, null, null), await ColumnsAsync(world, item.Id));
    }

    [Theory]
    [InlineData("next friday", "neither a date (YYYY-MM-DD) nor an ISO date-time")]
    [InlineData("2026-02-30", "is not a calendar day")]
    [InlineData("2026-11-20T17:00", "has no offset")]
    public async Task Dates_an_unparseable_due_is_refused_on_create_naming_both_forms(string due, string problem)
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", "Badly dated", Parent: "1", VerifyCmd: "dotnet test", Criteria: ["it holds"], Due: due)], Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains(problem, refused.Text, StringComparison.Ordinal);
        Assert.Contains("due:\"2026-11-20\"}])", refused.Text, StringComparison.Ordinal);
        Assert.Contains("due:\"2026-11-20T17:00:00+01:00\"}])", refused.Text, StringComparison.Ordinal);
        Assert.Equal(0, await CountTitledAsync(world, "Badly dated"));
    }

    [Fact]
    public async Task Dates_not_before_after_due_is_refused_on_create_and_on_update()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        var refused = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("task", "Backwards", Parent: "1", VerifyCmd: "dotnet test", Criteria: ["it holds"],
                Due: "2026-11-20", NotBefore: "2026-11-21")], Ct);
        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("not_before 2026-11-21 is after due 2026-11-20", refused.Text, StringComparison.Ordinal);
        Assert.Equal(0, await CountTitledAsync(world, "Backwards"));

        // The same day is fine: actionable from its start, due by its end.
        var sameDay = await Update(world, new UpdateArgs(Due: "2026-11-20", NotBefore: "2026-11-20"));
        Assert.False(sameDay.Refused, sameDay.Text);

        // On update the stored due is what a new not_before is judged against.
        var before = (await world.ItemAsync("1.1")).Revision;
        var late = await Update(world, new UpdateArgs(NotBefore: "2026-11-21T00:00:00Z"));
        Assert.True(late.Refused, late.Text);
        RefusalShape.SelfCorrecting(late.Text);
        Assert.Contains("work_update(ref:\"1.1\", not_before:\"<on or before 2026-11-20>\")", late.Text, StringComparison.Ordinal);
        Assert.Equal(before, (await world.ItemAsync("1.1")).Revision);
    }

    [Fact]
    public async Task Dates_work_update_journals_each_change_null_leaves_a_date_and_an_empty_string_clears_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        Assert.False((await Update(world, new UpdateArgs(Due: "2026-11-20"))).Refused);
        Assert.False((await Update(world, new UpdateArgs(Title: "Renamed"))).Refused);
        Assert.Equal("2026-11-20", (await ColumnsAsync(world, world.TaskId)).DueOn);

        Assert.False((await Update(world, new UpdateArgs(Due: "2026-11-20"))).Refused);
        var cleared = await Update(world, new UpdateArgs(Due: ""));
        Assert.False(cleared.Refused, cleared.Text);
        Assert.Null((await ColumnsAsync(world, world.TaskId)).DueOn);

        var changes = await PayloadsAsync(world, world.TaskId, "dates_changed");
        Assert.Equal(
            new (string?, string?, string?)[] { ("due", null, "2026-11-20"), ("due", "2026-11-20", null) },
            changes.Select(c => (
                c.GetProperty("field").GetString(),
                c.GetProperty("from").GetString(),
                c.GetProperty("to").GetString())));

        var updated = await PayloadsAsync(world, world.TaskId, "updated");
        Assert.Contains(updated, u => u.GetProperty("fields").EnumerateArray().Any(f => f.GetString() == "due"));
    }

    [Fact]
    public async Task Dates_a_slip_is_a_due_moved_later_and_a_move_earlier_or_a_first_due_is_not()
    {
        var world = await WriteSeed.CreateAsync(fixture);

        foreach (var due in new[] { "2026-11-20", "2026-11-25", "2026-11-22", "2026-11-22T23:30:00Z", "2026-11-30" })
        {
            var written = await Update(world, new UpdateArgs(Due: due));
            Assert.False(written.Refused, written.Text);
        }

        // set (none) → 25 later → 22 earlier → 22T23:30Z earlier than the end of the 22nd in UTC → 30 later: two slips.
        var text = await GetAsync(world, At(2026, 11, 1));
        Assert.Contains("  dates     due 2026-11-30 (UTC)", text, StringComparison.Ordinal);
        Assert.Contains("slipped 2× (due moved later)", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dates_a_day_is_overdue_only_after_it_ends_in_the_workspace_zone_and_a_zone_change_re_reads_it()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.False((await Update(world, new UpdateArgs(Due: "2026-11-20"))).Refused);
        await ZoneAsync(world, "Europe/Rome");

        // 23:30 UTC on the 20th is 00:30 on the 21st in Rome: the day is over there, though not yet in UTC.
        var past = At(2026, 11, 20, 23, 30);
        var text = await GetAsync(world, past);
        Assert.Contains("  dates     due 2026-11-20 (Europe/Rome), overdue", text, StringComparison.Ordinal);
        Assert.True(await OverdueAsync(world, past));

        var stillThatDay = At(2026, 11, 20, 22, 30);
        Assert.DoesNotContain("overdue", await GetAsync(world, stillThatDay), StringComparison.Ordinal);
        Assert.False(await OverdueAsync(world, stillThatDay));

        // New York is behind UTC: at the same instant it is still the evening of the 20th there.
        await ZoneAsync(world, "America/New_York");
        text = await GetAsync(world, past);
        Assert.Contains("  dates     due 2026-11-20 (America/New_York)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("overdue", text, StringComparison.Ordinal);
        Assert.False(await OverdueAsync(world, past));
    }

    [Fact]
    public async Task Dates_not_before_reads_as_not_yet_actionable_until_its_day_starts_in_the_zone()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.False((await Update(world, new UpdateArgs(NotBefore: "2026-11-21"))).Refused);
        await ZoneAsync(world, "Europe/Rome");

        Assert.Contains("not_before 2026-11-21 (Europe/Rome), not yet actionable",
            await GetAsync(world, At(2026, 11, 20, 22, 30)), StringComparison.Ordinal);
        Assert.DoesNotContain("not yet actionable", await GetAsync(world, At(2026, 11, 20, 23, 30)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dates_an_item_closed_after_its_due_reads_late_and_one_closed_in_time_does_not()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.False((await Update(world, new UpdateArgs(Due: "2026-11-20T12:00:00Z"))).Refused);

        await CloseAsync(world, At(2026, 11, 20, 11, 0));
        var text = await GetAsync(world, At(2026, 12, 1));
        Assert.Contains("  dates     due 2026-11-20T12:00:00Z", text, StringComparison.Ordinal);
        Assert.DoesNotContain("late —", text, StringComparison.Ordinal);
        Assert.DoesNotContain("overdue", text, StringComparison.Ordinal);

        await CloseAsync(world, At(2026, 11, 20, 12, 5));
        Assert.Contains("late — closed 2026-11-20 12:05 UTC, after its due", await GetAsync(world, At(2026, 12, 1)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dates_an_item_without_dates_prints_no_date_line()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.DoesNotContain("  dates     ", await GetAsync(world, At(2026, 11, 20)), StringComparison.Ordinal);
    }

    private static Task<WriteResult> Update(WriteWorld world, UpdateArgs args)
        => world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, "1.1", args, Ct);

    private static DateTimeOffset At(int year, int month, int day, int hour = 0, int minute = 0)
        => new(year, month, day, hour, minute, 0, TimeSpan.Zero);

    private static async Task<string> GetAsync(WriteWorld world, DateTimeOffset now)
    {
        var reader = new BoardReader(
            new WorkmanQueries(world.Fixture.DataSource),
            new BoardQueries(world.Fixture.DataSource),
            new FixedClock(now));
        var scope = (ScopeResult.Found)await reader.ResolveAsync(world.Slug, "1.1", Ct);
        return await reader.GetAsync(scope, ["1.1"], [], Ct);
    }

    /// <summary>The SQL work_agenda will ask: the one evaluation of "overdue now", read straight from the function.</summary>
    private static async Task<bool> OverdueAsync(WriteWorld world, DateTimeOffset now)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<bool>(
            $"SELECT {DateValue.DueEndSql("i")} <= @now FROM item i WHERE i.id = @id;",
            new { id = world.TaskId, now });
    }

    private static async Task ZoneAsync(WriteWorld world, string zone)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            "UPDATE workspace SET settings = jsonb_set(settings, '{timezone}', to_jsonb(@zone::text)) WHERE id = @id;",
            new { id = world.WorkspaceId, zone });
    }

    private static async Task CloseAsync(WriteWorld world, DateTimeOffset at)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            "UPDATE item SET status = 'done', resolution = 'accepted', closed_at = @at WHERE id = @id;",
            new { id = world.TaskId, at });
    }

    /// <summary>The four columns as text (an instant in UTC); they live outside the EF model, in the ItemDates migration.</summary>
    private static async Task<(string? DueOn, string? DueAt, string? NotBeforeOn, string? NotBeforeAt)> ColumnsAsync(
        WriteWorld world, Guid itemId)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.QuerySingleAsync<(string?, string?, string?, string?)>(
            """
            SELECT to_char(due_on, 'YYYY-MM-DD'),
                   to_char(due_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI'),
                   to_char(not_before_on, 'YYYY-MM-DD'),
                   to_char(not_before_at AT TIME ZONE 'UTC', 'YYYY-MM-DD HH24:MI')
            FROM item WHERE id = @itemId;
            """,
            new { itemId });
    }

    private static async Task<int> CountTitledAsync(WriteWorld world, string title)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.ExecuteScalarAsync<int>(
            "SELECT count(*) FROM item WHERE workspace_id = @workspace AND title = @title;",
            new { workspace = world.WorkspaceId, title });
    }

    private static async Task<JsonElement> PayloadAsync(WriteWorld world, Guid itemId, string kind)
        => Assert.Single(await PayloadsAsync(world, itemId, kind));

    private static async Task<List<JsonElement>> PayloadsAsync(WriteWorld world, Guid itemId, string kind)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        var rows = await connection.QueryAsync<string>(
            "SELECT payload::text FROM event WHERE item_id = @itemId AND kind = @kind ORDER BY seq;",
            new { itemId, kind });
        return [.. rows.Select(r => JsonDocument.Parse(r).RootElement.Clone())];
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
