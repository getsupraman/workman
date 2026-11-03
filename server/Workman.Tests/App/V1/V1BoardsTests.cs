using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Workman.Core.Domain;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.App.V1.V1Http;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App.V1;

/// <summary><c>/api/v1/boards</c>: many runs' boards in one read, each the one <c>/runs/{id}/board</c> returns.</summary>
[Collection(StoreCollection.Name)]
public sealed class V1BoardsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Each_batched_board_equals_that_runs_own_board_apart_from_asOf_and_seq()
    {
        var (v1, runB) = await TwoRunsAsync();
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        var boards = Array(await GetAsync(reader, "/api/v1/boards"), "boards");

        Assert.Equal([v1.World.RunId, runB], boards.Select(RunId));
        foreach (var batched in boards)
        {
            var own = await GetAsync(reader, $"/api/v1/runs/{RunId(batched)}/board");
            Assert.Equal(WithoutSnapshot(own), WithoutSnapshot(batched));
        }

        // The agreement is over boards that carry blockers, a cross-run one included, a claim and a commit.
        var b = Array(boards[1], "rows");
        Assert.Equal("1.4", Assert.Single(ByRef(b, "b.1").GetProperty("blockedBy").EnumerateArray()).GetProperty("ref").GetString());
        var a = Array(boards[0], "rows");
        Assert.NotEqual(JsonValueKind.Null, ByRef(a, "1.1").GetProperty("claim").ValueKind);
        Assert.Equal("a439808f00", ByRef(a, "1.1").GetProperty("commit").GetString());
    }

    [Fact]
    public async Task A_run_in_a_project_the_caller_is_not_a_member_of_is_absent()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;

        var u1 = await world.CookieAsync(world.User1);
        Assert.Equal([world.One.RunId], Array(await GetAsync(u1, "/api/v1/boards"), "boards").Select(RunId));
        Assert.Empty(Array(await GetAsync(u1, $"/api/v1/boards?runs={world.P2RunId}"), "boards"));

        var u2 = await world.CookieAsync(world.User2);
        Assert.Equal(
            new[] { world.One.RunId, world.P2RunId }.Order(),
            Array(await GetAsync(u2, "/api/v1/boards"), "boards").Select(RunId).Order());
    }

    [Fact]
    public async Task An_archived_projects_runs_are_left_out_unless_asked_for_by_id()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using (var db = fixture.NewContext())
        {
            (await db.Items.FindAsync([v1.World.ProjectId], Ct))!.ArchivedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(Ct);
        }

        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        Assert.Empty(Array(await GetAsync(reader, "/api/v1/boards"), "boards"));
        Assert.Equal(
            [v1.World.RunId],
            Array(await GetAsync(reader, $"/api/v1/boards?runs={v1.World.RunId}"), "boards").Select(RunId));
    }

    [Fact]
    public async Task Runs_filters_and_leaves_out_ids_that_are_no_visible_run()
    {
        var (v1, runB) = await TwoRunsAsync();
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        Assert.Equal([runB], Array(await GetAsync(reader, $"/api/v1/boards?runs={runB}"), "boards").Select(RunId));

        var mixed = $"/api/v1/boards?runs={runB},{Guid.CreateVersion7()},{v1.World.PhaseId},{v1.World.RunId}";
        Assert.Equal([runB, v1.World.RunId], Array(await GetAsync(reader, mixed), "boards").Select(RunId));
    }

    [Fact]
    public async Task More_runs_than_the_cap_or_a_malformed_id_is_a_400()
    {
        var v1 = await V1World.CreateAsync(fixture);
        await using var server = await v1.StartAsync(fixture);
        var reader = Reader(server);

        var atCap = string.Join(',', Enumerable.Range(0, 200).Select(_ => Guid.CreateVersion7()));
        Assert.Empty(Array(await GetAsync(reader, $"/api/v1/boards?runs={atCap}"), "boards"));

        var over = string.Join(',', Enumerable.Range(0, 201).Select(_ => Guid.CreateVersion7()));
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync($"/api/v1/boards?runs={over}", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await reader.GetAsync("/api/v1/boards?runs=not-a-guid", Ct)).StatusCode);
    }

    /// <summary><see cref="V1World"/> plus a second run whose task waits on run-a's open 1.4.</summary>
    private async Task<(V1World V1, Guid RunB)> TwoRunsAsync()
    {
        var v1 = await V1World.CreateAsync(fixture);
        var runB = await WriteSeed.ChildAsync(v1.World, v1.World.ProjectId, "run-b", kind: "run");
        var task = await WriteSeed.ChildAsync(v1.World, runB, "b.1");
        var gate = Assert.Single(await PhaseChildrenAsync(v1, "1.4"));
        await WriteSeed.LinkAsync(v1.World, gate, task, LinkType.Blocks);
        return (v1, runB);
    }

    private async Task<List<Guid>> PhaseChildrenAsync(V1World v1, string reference)
    {
        await using var db = fixture.NewContext();
        return [.. db.Items.Where(i => i.ParentId == v1.World.PhaseId && i.Ref == reference).Select(i => i.Id)];
    }

    private static Guid RunId(JsonElement board) => board.GetProperty("run").GetProperty("id").GetGuid();

    private static string WithoutSnapshot(JsonElement board)
    {
        var node = JsonNode.Parse(board.GetRawText())!.AsObject();
        Assert.True(node.Remove("asOf") && node.Remove("seq"));
        return node.ToJsonString();
    }
}
