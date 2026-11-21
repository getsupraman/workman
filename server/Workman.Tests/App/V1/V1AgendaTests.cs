using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Workman.Core.Domain;
using Workman.Core.Writing;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.App.V1.V1Http;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary>
/// deadlines/6: <c>GET /api/v1/agenda</c> renders work_agenda's three lists as JSON, as of the injected clock (C242),
/// for the projects the caller can see (rule 8); board rows and the item page carry <c>due</c>, <c>notBefore</c>,
/// <c>late</c> and <c>slips</c>, and an undated item reads null, null, false, 0.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1AgendaTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Agenda_lists_overdue_due_and_actionable_in_the_workspace_zone_as_of_the_injected_clock()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await ExecAsync("UPDATE workspace SET settings = jsonb_set(settings, '{timezone}', '\"Europe/Rome\"') WHERE id = @id;", new { id = world.WorkspaceId });
        await DueAsync(world.TaskId, "2026-11-20");
        await DueAsync(await WriteSeed.ChildAsync(world, world.PhaseId, "1.2"), "2026-11-22");
        await DueAsync(await WriteSeed.ChildAsync(world, world.PhaseId, "1.3"), "2026-11-23");
        var blocked = await WriteSeed.ChildAsync(world, world.PhaseId, "1.5");
        await DueAsync(blocked, "2026-11-19");
        var blocker = await WriteSeed.ChildAsync(world, world.PhaseId, "1.6");
        await WriteSeed.LinkAsync(world, blocker, blocked, LinkType.Blocks);
        var waited = await WriteSeed.ChildAsync(world, world.PhaseId, "1.7");
        await ExecAsync("UPDATE item SET not_before_on = DATE '2026-11-20' WHERE id = @id;", new { id = waited });
        await ExecAsync("UPDATE item SET opened_at = '2026-01-01T00:00:00Z' WHERE workspace_id = @id;", new { id = world.WorkspaceId });

        // 23:30 UTC on the 20th is already the 21st in Rome: the 20th is over there.
        var now = new DateTimeOffset(2026, 11, 20, 23, 30, 0, TimeSpan.Zero);
        await using var server = await AppServer.StartAsync(
            fixture.ConnectionString, world.Slug, Ct,
            configure: b => b.Services.AddSingleton<TimeProvider>(new FixedClock(now)));
        var reader = await ReaderAsync(server);

        var agenda = await GetAsync(reader, "/api/v1/agenda?until=2026-11-22&since=2026-11-20", now);
        Assert.Equal(now, agenda.GetProperty("asOf").GetDateTimeOffset());
        Assert.True(agenda.GetProperty("seq").GetInt64() >= 0);
        Assert.Equal("Europe/Rome", agenda.GetProperty("zone").GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 22, 23, 0, 0, TimeSpan.Zero), agenda.GetProperty("until").GetDateTimeOffset());
        Assert.Equal(new DateTimeOffset(2026, 11, 19, 23, 0, 0, TimeSpan.Zero), agenda.GetProperty("since").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, agenda.GetProperty("scope").ValueKind);

        Assert.Equal(["1.5", "1.1"], Refs(agenda, "overdue"));
        Assert.Equal(["1.2"], Refs(agenda, "due"));
        Assert.Contains("1.7", Refs(agenda, "actionable"));
        Assert.DoesNotContain("1.5", Refs(agenda, "actionable"));

        var late = ByRef(Array(agenda, "overdue"), "1.5");
        Assert.Equal(blocked, late.GetProperty("item").GetProperty("id").GetGuid());
        Assert.Equal("open", late.GetProperty("status").GetString());
        Assert.Equal("2026-11-19", late.GetProperty("due").GetString());
        Assert.Equal(JsonValueKind.Null, late.GetProperty("notBefore").ValueKind);
        Assert.Equal(JsonValueKind.Null, late.GetProperty("holder").ValueKind);
        Assert.Equal(blocker, Assert.Single(Array(late, "blockers")).GetProperty("id").GetGuid());
        Assert.Equal("2026-11-20", ByRef(Array(agenda, "actionable"), "1.7").GetProperty("notBefore").GetString());

        // A scope is an id, as every v1 URL takes; the run's lists are the workspace's here.
        var scoped = await GetAsync(reader, $"/api/v1/agenda?until=2026-11-22&scope={world.RunId}", now);
        Assert.Equal(world.RunId, scoped.GetProperty("scope").GetProperty("id").GetGuid());
        Assert.Equal(["1.5", "1.1"], Refs(scoped, "overdue"));

        // An instant until is an instant: 22:00 UTC on the 22nd is before the 22nd ends in Rome; midnight there is its end.
        Assert.Empty(Refs(await GetAsync(reader, "/api/v1/agenda?until=2026-11-22T22:00:00Z", now), "due"));
        Assert.Equal(["1.2"], Refs(await GetAsync(reader, "/api/v1/agenda?until=2026-11-23T00:00:00%2B01:00", now), "due"));

        Assert.Equal(HttpStatusCode.NotFound, (await reader.GetAsync($"/api/v1/agenda?until=2026-11-22&scope={Guid.CreateVersion7()}", Ct)).StatusCode);
        foreach (var bad in new[] { "/api/v1/agenda", "/api/v1/agenda?until=tomorrow", "/api/v1/agenda?until=2026-11-20T17:00",
                     "/api/v1/agenda?until=2026-02-30", "/api/v1/agenda?until=2026-11-22&since=yesterday" })
        {
            var response = await reader.GetAsync(bad, Ct);
            Assert.True(response.StatusCode == HttpStatusCode.BadRequest, $"{bad} → {(int)response.StatusCode}");
            var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("error").GetString()!;
            Assert.Contains("YYYY-MM-DD", error, StringComparison.Ordinal);
            Assert.Contains("ISO date-time with an offset", error, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Agenda_lists_nothing_of_a_project_the_caller_is_not_a_member_of()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await ExecAsync(
            """
            UPDATE item SET due_on = DATE '2026-01-05' WHERE id = @p1Task;
            UPDATE item SET due_on = DATE '2026-01-06' WHERE id = @p2Task;
            UPDATE item SET due_on = DATE '2026-01-07' WHERE id = @p2Only;
            """,
            new { p1Task = world.One.TaskId, p2Task = world.P2TaskId, p2Only = world.OnlyInP2Id });
        const string Path = "/api/v1/agenda?until=2026-01-31&since=2000-01-01";

        foreach (var credential in await world.CredentialsAsync(world.User1))
        {
            var agenda = await GetAsync(credential.Http, Path);
            Assert.Equal([world.One.TaskId], Ids(agenda, "overdue"));
            var text = agenda.ToString();
            foreach (var id in world.P2Ids)
            {
                Assert.False(text.Contains(id.ToString(), StringComparison.OrdinalIgnoreCase), $"{credential} agenda shows P2's {id}");
            }

            Assert.DoesNotContain(ProjectWorld.TitleP2, text, StringComparison.Ordinal);
            Assert.DoesNotContain(ProjectWorld.TitleOnlyInP2, text, StringComparison.Ordinal);

            var hidden = await credential.Http.GetAsync($"{Path}&scope={world.P2RunId}", Ct);
            Assert.True(hidden.StatusCode == HttpStatusCode.NotFound, $"{credential} P2 scope → {(int)hidden.StatusCode}");
        }

        var u2 = (await world.CredentialsAsync(world.User2))[0];
        foreach (var credential in new[] { u2, world.Static })
        {
            var agenda = await GetAsync(credential.Http, Path);
            Assert.Equal([world.One.TaskId, world.P2TaskId, world.OnlyInP2Id], Ids(agenda, "overdue"));
            Assert.Equal(
                [world.P2TaskId, world.OnlyInP2Id],
                Ids(await GetAsync(credential.Http, $"{Path}&scope={world.P2RunId}"), "overdue"));
        }
    }

    [Fact]
    public async Task Board_rows_and_the_item_page_carry_due_not_before_late_and_slips()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        foreach (var due in new[] { "2026-11-25", "2026-11-30", "2026-11-28", "2026-12-04" })
        {
            var updated = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, "1.1", new UpdateArgs(Due: due), Ct);
            Assert.False(updated.Refused, updated.Text);
        }

        var nb = await world.Writer.UpdateAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", new UpdateArgs(NotBefore: "2026-11-20T08:00:00+01:00"), Ct);
        Assert.False(nb.Refused, nb.Text);

        var closed = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Done);
        await ExecAsync("UPDATE item SET due_on = DATE '2026-01-01' WHERE id = @id;", new { id = closed });
        var undated = await WriteSeed.ChildAsync(world, world.PhaseId, "1.3");

        await using var server = await StartAsync(fixture, world.Slug);
        var reader = await ReaderAsync(server);
        var rows = Array(await GetAsync(reader, $"/api/v1/runs/{world.RunId}/board"), "rows");

        // set, later, earlier, later: two slips.
        AssertDates(ByRef(rows, "1.1"), "2026-12-04", "2026-11-20T07:00:00Z", late: false, slips: 2);
        AssertDates(ByRef(rows, "1.2"), "2026-01-01", null, late: true, slips: 0);
        AssertDates(ByRef(rows, "1.3"), null, null, late: false, slips: 0);

        AssertDates(await GetAsync(reader, $"/api/v1/items/{world.TaskId}"), "2026-12-04", "2026-11-20T07:00:00Z", late: false, slips: 2);
        AssertDates(await GetAsync(reader, $"/api/v1/items/{closed}"), "2026-01-01", null, late: true, slips: 0);
        AssertDates(await GetAsync(reader, $"/api/v1/items/{undated}"), null, null, late: false, slips: 0);
    }

    private static void AssertDates(JsonElement json, string? due, string? notBefore, bool late, int slips)
    {
        Assert.Equal(due, json.GetProperty("due").GetString());
        Assert.Equal(notBefore, json.GetProperty("notBefore").GetString());
        Assert.Equal(late, json.GetProperty("late").GetBoolean());
        Assert.Equal(slips, json.GetProperty("slips").GetInt32());
    }

    private static List<string> Refs(JsonElement agenda, string list)
        => [.. Array(agenda, list).Select(r => r.GetProperty("item").GetProperty("ref").GetString()!)];

    private static List<Guid> Ids(JsonElement agenda, string list)
        => [.. Array(agenda, list).Select(r => r.GetProperty("item").GetProperty("id").GetGuid())];

    private Task DueAsync(Guid itemId, string day)
        => ExecAsync("UPDATE item SET due_on = @day::date WHERE id = @id;", new { id = itemId, day });

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
