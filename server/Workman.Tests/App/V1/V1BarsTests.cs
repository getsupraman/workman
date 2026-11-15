using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.App.V1.V1Http;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary>
/// <c>/api/v1/bars</c>: what the sidebar's and the overview's run bars draw, read in a fixed number of SQL commands,
/// and the same leaves, state inputs, claim holder and close the dashboard used to take from every full board.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class V1BarsTests(PostgresFixture fixture)
{
    /// <summary>
    /// Generous on purpose: the point is that it does not move with the runs. Today the read takes about twenty
    /// (each query is two: the tenant's set_config and the statement).
    /// </summary>
    private const int Bound = 40;

    private static readonly HashSet<string> Leaf = ["task", "followup", "subtask", "spike", "question", "incident"];

    private static readonly HashSet<string> EmptyCounted = ["phase", "feature"];

    private static readonly HashSet<string> Closed = ["done", "cancelled", "superseded"];

    [Fact]
    public async Task The_landing_reads_issue_as_many_commands_for_150_runs_as_for_10()
    {
        var small = await BarsSeed.CreateAsync(fixture, 10);
        var large = await BarsSeed.CreateAsync(fixture, 150);
        Assert.True(large.Items >= 2000, $"the large seed holds {large.Items} items");

        var counts = new Dictionary<string, (int Small, int Large)>();
        foreach (var path in new[] { "/api/v1/bars", "/api/v1/boards", "/api/v1/overview", "/api/v1/inbox" })
        {
            counts[path] = (await CommandsAsync(small, path), await CommandsAsync(large, path));
            SqlCommands.Log($"{path}: {counts[path].Small} commands for 10 runs, {counts[path].Large} for 150");
        }

        foreach (var (path, (smallCount, largeCount)) in counts)
        {
            Assert.True(smallCount > 0, $"{path}: no command counted, so the counter saw nothing");
            Assert.True(smallCount == largeCount, $"{path}: {smallCount} commands for 10 runs, {largeCount} for 150");
        }

        Assert.True(counts["/api/v1/bars"].Large <= Bound, $"/api/v1/bars: {counts["/api/v1/bars"].Large} commands");
    }

    [Fact]
    public async Task A_subset_of_runs_costs_what_every_run_costs()
    {
        var world = await BarsSeed.CreateAsync(fixture, 30);
        await using var server = await StartAsync(fixture, world.Slug);
        var reader = await ReaderAsync(server);
        await SqlCommands.GetAsync(reader, "/api/v1/sessions");

        var all = await SqlCommands.GetAsync(reader, "/api/v1/bars");
        var one = await SqlCommands.GetAsync(reader, $"/api/v1/bars?runs={world.RunIds[4]}");

        Assert.Equal(30, Array(all.Body, "runs").Count);
        Assert.Equal([world.RunIds[4]], Array(one.Body, "runs").Select(r => r.GetProperty("run").GetGuid()));
        Assert.True(one.Commands <= all.Commands, $"{one.Commands} commands for one run, {all.Commands} for all");
    }

    [Fact]
    public async Task Each_runs_bars_equal_the_tally_of_its_board()
    {
        var world = await BarsSeed.CreateAsync(fixture, 150);
        await using var server = await StartAsync(fixture, world.Slug);
        var reader = await ReaderAsync(server);

        await AgreeAsync(reader, "");

        // Every state a bar draws is in the comparison, not only the easy ones.
        var leaves = Array(await GetAsync(reader, "/api/v1/bars"), "runs").SelectMany(r => Array(r, "leaves")).ToList();
        Assert.Contains(leaves, l => l.GetProperty("proof").GetBoolean());
        Assert.Contains(leaves, l => l.GetProperty("claimExpired").GetBoolean());
        Assert.Contains(leaves, l => l.GetProperty("waitingOn").GetArrayLength() > 0 && l.GetProperty("waitingOn")[0].GetString()!.Contains('/'));
        Assert.Contains(leaves, l => l.GetProperty("resolution").GetString() == "obsolete");
        Assert.Contains(leaves, l => l.GetProperty("kind").GetString() == "phase");
    }

    [Fact]
    public async Task The_v1_worlds_bars_equal_the_tally_of_its_boards()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = await ReaderAsync(server);

        await AgreeAsync(reader, "");
        await AgreeAsync(reader, $"?runs={v1.World.RunId}");

        var run = Assert.Single(Array(await GetAsync(reader, "/api/v1/bars"), "runs"));
        Assert.Equal("1.1", run.GetProperty("holder").GetProperty("item").GetProperty("ref").GetString());
        Assert.Equal(V1World.SessionName, run.GetProperty("holder").GetProperty("session").GetString());
    }

    [Fact]
    public async Task A_run_in_a_project_the_caller_is_not_a_member_of_is_absent()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;

        var u1 = await world.CookieAsync(world.User1);
        Assert.Equal([world.One.RunId], Runs(await GetAsync(u1, "/api/v1/bars")));
        Assert.Empty(Runs(await GetAsync(u1, $"/api/v1/bars?runs={world.P2RunId}")));

        var u2 = await world.CookieAsync(world.User2);
        Assert.Equal(new[] { world.One.RunId, world.P2RunId }.Order(), Runs(await GetAsync(u2, "/api/v1/bars")).Order());
    }

    [Fact]
    public async Task Another_workspaces_runs_do_not_leak()
    {
        var other = await BarsSeed.CreateAsync(fixture, 10);
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = await ReaderAsync(server);

        Assert.Equal([v1.World.RunId], Runs(await GetAsync(reader, "/api/v1/bars")));
        Assert.Empty(Runs(await GetAsync(reader, $"/api/v1/bars?runs={string.Join(',', other.RunIds)}")));

        var body = (await GetAsync(reader, $"/api/v1/bars?runs={v1.World.RunId},{other.RunIds[0]}")).GetRawText();
        Assert.DoesNotContain("bars seed", body);
    }

    [Fact]
    public async Task More_runs_than_the_cap_or_a_malformed_id_is_a_400()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = await ReaderAsync(server);

        var over = string.Join(',', Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync($"/api/v1/bars?runs={over}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/v1/bars?runs=not-a-guid", Ct)).StatusCode);
    }

    private async Task<int> CommandsAsync(BarsWorld world, string path)
    {
        await using var server = await StartAsync(fixture, world.Slug);
        var reader = await ReaderAsync(server);

        // The first request of a host pays for its own start-up; count the second.
        await SqlCommands.GetAsync(reader, "/api/v1/sessions");
        var counted = await SqlCommands.GetAsync(reader, path);
        SqlCommands.Log($"{path}, {world.RunIds.Count} runs: {counted.Commands} commands, {counted.Elapsed.TotalMilliseconds:0} ms");
        SqlCommands.Log(SqlCommands.Slowest(counted));
        return counted.Commands;
    }

    /// <summary><c>/bars</c> against what <c>state.ts</c>'s <c>tally()</c> and RunStrip read off <c>/boards</c>, run by run.</summary>
    private static async Task AgreeAsync(HttpClient reader, string query)
    {
        var boards = Array(await GetAsync(reader, $"/api/v1/boards{query}"), "boards");
        var bars = Array(await GetAsync(reader, $"/api/v1/bars{query}"), "runs");

        Assert.Equal(boards.Select(b => b.GetProperty("run").GetProperty("id").GetGuid()), bars.Select(b => b.GetProperty("run").GetGuid()));
        Assert.NotEmpty(boards);

        foreach (var (board, bar) in boards.Zip(bars))
        {
            Assert.Equal(Expected(board), Actual(bar));
        }
    }

    /// <summary>The leaves <c>tally()</c> keeps, each as <c>rowInput()</c> reads it, the holder RunStrip finds, and <c>rows[0].closedAt</c>.</summary>
    private static string Expected(JsonElement board)
    {
        var rows = Array(board, "rows");
        var leaves = new JsonArray();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var item = row.GetProperty("item");
            var kind = item.GetProperty("kind").GetString()!;
            var counted = Leaf.Contains(kind)
                || (EmptyCounted.Contains(kind) && !row.GetProperty("hasChildren").GetBoolean()
                    && !(i + 1 < rows.Count && rows[i + 1].GetProperty("depth").GetInt32() > row.GetProperty("depth").GetInt32()));
            if (!counted)
            {
                continue;
            }

            var verification = row.GetProperty("verification");
            leaves.Add(Leaf_(
                item.GetProperty("id").GetGuid(),
                item.GetProperty("qualifiedRef").GetString()!,
                item.GetProperty("title").GetString()!,
                kind,
                item.GetProperty("status").GetString()!,
                row.GetProperty("resolution").GetString(),
                row.GetProperty("claim") is { ValueKind: JsonValueKind.Object } claim && claim.GetProperty("expired").GetBoolean(),
                verification.ValueKind == JsonValueKind.Object && verification.GetProperty("passed").GetBoolean()
                    && verification.GetProperty("runBy").GetString() != "worker",
                [.. Array(row, "blockedBy").Select(b => b.GetProperty("qualifiedRef").GetString()!)]));
        }

        var holding = rows.FirstOrDefault(r => r.GetProperty("claim").ValueKind == JsonValueKind.Object
            && !Closed.Contains(r.GetProperty("item").GetProperty("status").GetString()!));
        JsonNode? holder = null;
        if (holding.ValueKind == JsonValueKind.Object)
        {
            var claim = holding.GetProperty("claim");
            var session = claim.GetProperty("heldBySession").GetString()
                ?? (claim.GetProperty("session") is { ValueKind: JsonValueKind.Object } s ? s.GetProperty("name").GetString() : null);
            holder = Holder(holding.GetProperty("item"), session, claim.GetProperty("role").GetString()!);
        }

        return Shape(rows[0].GetProperty("closedAt"), holder, leaves);
    }

    private static string Actual(JsonElement bar)
    {
        var leaves = new JsonArray();
        foreach (var leaf in Array(bar, "leaves"))
        {
            leaves.Add(Leaf_(
                leaf.GetProperty("id").GetGuid(),
                leaf.GetProperty("qualifiedRef").GetString()!,
                leaf.GetProperty("title").GetString()!,
                leaf.GetProperty("kind").GetString()!,
                leaf.GetProperty("status").GetString()!,
                leaf.GetProperty("resolution").GetString(),
                leaf.GetProperty("claimExpired").GetBoolean(),
                leaf.GetProperty("proof").GetBoolean(),
                [.. Array(leaf, "waitingOn").Select(w => w.GetString()!)]));
        }

        var holder = bar.GetProperty("holder") is { ValueKind: JsonValueKind.Object } h
            ? Holder(h.GetProperty("item"), h.GetProperty("session").GetString(), h.GetProperty("role").GetString()!)
            : null;

        return Shape(bar.GetProperty("closedAt"), holder, leaves);
    }

    private static JsonObject Leaf_(
        Guid id, string qualifiedRef, string title, string kind, string status, string? resolution, bool claimExpired, bool proof, string[] waitingOn)
        => new()
        {
            ["id"] = id,
            ["qualifiedRef"] = qualifiedRef,
            ["title"] = title,
            ["kind"] = kind,
            ["status"] = status,
            ["resolution"] = resolution,
            ["claimExpired"] = claimExpired,
            ["proof"] = proof,
            ["waitingOn"] = new JsonArray([.. waitingOn.Select(w => (JsonNode?)w)]),
        };

    private static JsonObject Holder(JsonElement item, string? session, string role)
        => new()
        {
            ["item"] = JsonNode.Parse(item.GetRawText()),
            ["session"] = session,
            ["role"] = role,
        };

    private static string Shape(JsonElement closedAt, JsonNode? holder, JsonArray leaves)
        => new JsonObject
        {
            ["closedAt"] = JsonNode.Parse(closedAt.GetRawText()),
            ["holder"] = holder,
            ["leaves"] = leaves,
        }.ToJsonString();

    private static IEnumerable<Guid> Runs(JsonElement bars) => Array(bars, "runs").Select(r => r.GetProperty("run").GetGuid());
}
