using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Workman.Core.Domain;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.App.V1.V1Http;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary>
/// account-calendar/3: <c>GET /api/v1/calendar</c> lists every dated item whose due or not_before falls in a range of
/// days read in the workspace's zone, open and done alike, for the projects the caller can see and never an archived
/// one's; bad dates and a range over the limit are a 400 saying what to send instead.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1CalendarTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Calendar_lists_open_and_closed_dated_items_of_the_range_in_the_workspace_zone()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await ExecAsync("UPDATE workspace SET settings = jsonb_set(settings, '{timezone}', '\"Europe/Rome\"') WHERE id = @id;", new { id = world.WorkspaceId });

        // 1.1: due the 20th. 1.2: 23:30 UTC on the 19th is already the 20th in Rome. 1.3: 23:30 UTC on the 20th is the 21st there.
        await ExecAsync("UPDATE item SET due_on = DATE '2026-11-20' WHERE id = @id;", new { id = world.TaskId });
        var earlyInstant = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await ExecAsync("UPDATE item SET due_at = '2026-11-19T23:30:00Z' WHERE id = @id;", new { id = earlyInstant });
        var lateInstant = await WriteSeed.ChildAsync(world, world.PhaseId, "1.3");
        await ExecAsync("UPDATE item SET due_at = '2026-11-20T23:30:00Z' WHERE id = @id;", new { id = lateInstant });

        // Done the day after its due day: late. Done on its due day: not late.
        var doneLate = await WriteSeed.ChildAsync(world, world.PhaseId, "1.4", status: ItemStatus.Done);
        await ExecAsync("UPDATE item SET due_on = DATE '2026-11-20', closed_at = '2026-11-21T10:00:00Z' WHERE id = @id;", new { id = doneLate });
        var doneOnTime = await WriteSeed.ChildAsync(world, world.PhaseId, "1.5", status: ItemStatus.Done);
        await ExecAsync("UPDATE item SET due_on = DATE '2026-11-20', closed_at = '2026-11-20T10:00:00Z' WHERE id = @id;", new { id = doneOnTime });

        // Only a not_before on the 20th is in; a cancelled item and the days either side are out.
        var starts = await WriteSeed.ChildAsync(world, world.PhaseId, "1.6");
        await ExecAsync("UPDATE item SET not_before_on = DATE '2026-11-20' WHERE id = @id;", new { id = starts });
        var cancelled = await WriteSeed.ChildAsync(world, world.PhaseId, "1.7", status: ItemStatus.Cancelled);
        await ExecAsync("UPDATE item SET due_on = DATE '2026-11-20' WHERE id = @id;", new { id = cancelled });
        var before = await WriteSeed.ChildAsync(world, world.PhaseId, "1.8");
        await ExecAsync("UPDATE item SET due_on = DATE '2026-11-19' WHERE id = @id;", new { id = before });
        var after = await WriteSeed.ChildAsync(world, world.PhaseId, "1.9");
        await ExecAsync("UPDATE item SET due_on = DATE '2026-11-21' WHERE id = @id;", new { id = after });

        // A wait blocking 1.1 shows as its blocker.
        var wait = await WriteSeed.ChildAsync(world, world.PhaseId, "1.10", kind: "wait");
        await WriteSeed.LinkAsync(world, wait, world.TaskId, LinkType.Blocks);

        var now = new DateTimeOffset(2026, 11, 25, 9, 0, 0, TimeSpan.Zero);
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString, world.Slug, Ct,
            configure: b => b.Services.AddSingleton<TimeProvider>(new FixedClock(now)));
        var reader = await ReaderAsync(server);

        var calendar = await GetAsync(reader, "/api/v1/calendar?from=2026-11-20&to=2026-11-20");
        Assert.Equal(now, calendar.GetProperty("asOf").GetDateTimeOffset());
        Assert.Equal("Europe/Rome", calendar.GetProperty("zone").GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 23, 0, 0, TimeSpan.Zero), calendar.GetProperty("from").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 23, 0, 0, TimeSpan.Zero), calendar.GetProperty("to").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, calendar.GetProperty("scope").ValueKind);

        // Ordered by the earlier of due end and not_before start: 1.6 starts at the 20th's start, 1.2 is due 30 minutes on.
        Assert.Equal(["1.6", "1.2", "1.1", "1.4", "1.5"], Refs(calendar));

        var task = ByRef(Array(calendar, "items"), "1.1");
        Assert.Equal(world.TaskId, task.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal("task", task.GetProperty("kind").GetString());
        Assert.Equal("open", task.GetProperty("status").GetString());
        Assert.Equal("2026-11-20", task.GetProperty("due").GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 20, 23, 0, 0, TimeSpan.Zero), task.GetProperty("dueEnd").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, task.GetProperty("notBefore").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("closedAt").ValueKind);
        Assert.Equal(JsonValueKind.Null, task.GetProperty("holder").ValueKind);
        Assert.False(task.GetProperty("late").GetBoolean());
        var blocker = Assert.Single(Array(task, "blockers"));
        Assert.Equal(wait, blocker.GetProperty("id").GetGuid());
        Assert.Equal("wait", blocker.GetProperty("kind").GetString());

        Assert.Equal("2026-11-19T23:30:00Z", ByRef(Array(calendar, "items"), "1.2").GetProperty("due").GetString());

        var late = ByRef(Array(calendar, "items"), "1.4");
        Assert.Equal("done", late.GetProperty("status").GetString());
        Assert.True(late.GetProperty("late").GetBoolean());
        Assert.Equal(new DateTimeOffset(2026, 11, 21, 10, 0, 0, TimeSpan.Zero), late.GetProperty("closedAt").GetDateTimeOffset());
        Assert.False(ByRef(Array(calendar, "items"), "1.5").GetProperty("late").GetBoolean());

        var started = ByRef(Array(calendar, "items"), "1.6");
        Assert.Equal("2026-11-20", started.GetProperty("notBefore").GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 23, 0, 0, TimeSpan.Zero), started.GetProperty("notBeforeStart").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, started.GetProperty("due").ValueKind);

        // The next day holds 1.3 (its Rome day) and 1.9; a scope is an id.
        Assert.Equal(["1.3", "1.9"], Refs(await GetAsync(reader, "/api/v1/calendar?from=2026-11-21&to=2026-11-21")));
        var scoped = await GetAsync(reader, $"/api/v1/calendar?from=2026-11-20&to=2026-11-20&scope={world.RunId}");
        Assert.Equal(world.RunId, scoped.GetProperty("scope").GetProperty("id").GetGuid());
        Assert.Equal(["1.6", "1.2", "1.1", "1.4", "1.5"], Refs(scoped));
        // Instant bounds: 1.2's instant is before them, while the day items' day (the 20th in Rome) overlaps them.
        Assert.Equal(
            ["1.6", "1.1", "1.4", "1.5"],
            Refs(await GetAsync(reader, "/api/v1/calendar?from=2026-11-20T01:00:00%2B01:00&to=2026-11-20T12:00:00Z")));
        // A single instant: 1.2 due exactly then is in (to is inclusive); that instant is on the 20th in Rome, so the 20th's items are too.
        Assert.Equal(
            ["1.6", "1.2", "1.1", "1.4", "1.5"],
            Refs(await GetAsync(reader, "/api/v1/calendar?from=2026-11-19T23:30:00Z&to=2026-11-19T23:30:00Z")));
        Assert.Equal(
            HttpStatusCode.NotFound,
            (await reader.GetAsync($"/api/v1/calendar?from=2026-11-20&to=2026-11-20&scope={Guid.CreateVersion7()}", Ct)).StatusCode);

        // Exactly 100 days is read; 101 is not.
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync("/api/v1/calendar?from=2026-11-01&to=2027-02-08", Ct)).StatusCode);
        Assert.Contains("100 days", await BadAsync(reader, "/api/v1/calendar?from=2026-11-01&to=2027-02-09"), StringComparison.Ordinal);
        Assert.Contains("on or after from", await BadAsync(reader, "/api/v1/calendar?from=2026-11-20&to=2026-11-19"), StringComparison.Ordinal);
        foreach (var bad in new[]
                 {
                     "/api/v1/calendar", "/api/v1/calendar?from=2026-11-20", "/api/v1/calendar?to=2026-11-20",
                     "/api/v1/calendar?from=today&to=2026-11-20", "/api/v1/calendar?from=2026-11-20&to=2026-11-20T17:00",
                     "/api/v1/calendar?from=2026-02-30&to=2026-03-01",
                 })
        {
            var error = await BadAsync(reader, bad);
            Assert.Contains("YYYY-MM-DD", error, StringComparison.Ordinal);
            Assert.Contains("ISO date-time with an offset", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Calendar_lists_nothing_of_a_project_the_caller_cannot_see_or_one_archived()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await ExecAsync(
            """
            UPDATE item SET due_on = DATE '2026-01-05' WHERE id = @p1Task;
            UPDATE item SET due_on = DATE '2026-01-06', status = 'done', resolution = 'accepted', closed_at = '2026-01-07T00:00:00Z' WHERE id = @p2Task;
            UPDATE item SET not_before_on = DATE '2026-01-07' WHERE id = @p2Only;
            """,
            new { p1Task = world.One.TaskId, p2Task = world.P2TaskId, p2Only = world.OnlyInP2Id });
        const string Path = "/api/v1/calendar?from=2026-01-01&to=2026-01-31";

        foreach (var credential in await world.CredentialsAsync(world.User1))
        {
            var calendar = await GetAsync(credential.Http, Path);
            Assert.Equal([world.One.TaskId], Ids(calendar));
            var text = calendar.ToString();
            foreach (var id in world.P2Ids)
            {
                Assert.False(text.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase), $"{credential} calendar shows P2's {id}");
            }

            Assert.DoesNotContain(ProjectWorld.TitleP2, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ProjectWorld.TitleOnlyInP2, text, StringComparison.Ordinal);
            var hidden = await credential.Http.GetAsync($"{Path}&scope={world.P2RunId}", Ct);
            Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, $"{credential} P2 scope → {(int)hidden.StatusCode}");
        }

        var u2 = (await world.CredentialsAsync(world.User2))[0];
        foreach (var credential in new[] { u2, world.Static })
        {
            Assert.Equal([world.One.TaskId, world.P2TaskId, world.OnlyInP2Id], Ids(await GetAsync(credential.Http, Path)));
        }

        // An archived project drops out, its subtree too when asked for by id; restored, it is back.
        await ExecAsync("UPDATE item SET archived_at = now() WHERE id = @id;", new { id = world.P2Id });
        foreach (var credential in new[] { u2, world.Static })
        {
            Assert.Equal([world.One.TaskId], Ids(await GetAsync(credential.Http, Path)));
            Assert.Empty(Ids(await GetAsync(credential.Http, $"{Path}&scope={world.P2RunId}")));
        }

        await ExecAsync("UPDATE item SET archived_at = NULL WHERE id = @id;", new { id = world.P2Id });
        Assert.Equal([world.One.TaskId, world.P2TaskId, world.OnlyInP2Id], Ids(await GetAsync(u2.Http, Path)));
    }

    private static async Task<string> BadAsync(HttpClient client, string path)
    {
        var response = await client.GetAsync(path, Ct);
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{path} → {(int)response.StatusCode}");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString()!;
    }

    private static List<string> Refs(JsonElement calendar)
        => [.. Array(calendar, "items").Select(r => r.GetProperty("item").GetProperty("ref").GetString()!)];

    private static List<Guid> Ids(JsonElement calendar)
        => [.. Array(calendar, "items").Select(r => r.GetProperty("item").GetProperty("id").GetGuid())];

    private async Task ExecAsync(string sql, object parameters)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(sql, parameters);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
