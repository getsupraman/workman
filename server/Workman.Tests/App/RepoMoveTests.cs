using Dapper;
using Microsoft.EntityFrameworkCore;
using Workman.Core.Domain;
using Workman.Core.Writing;
using Workman.Tests.Read;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App;

/// <summary>
/// A repo moves between projects, or leaves one, through work_update — no database edit. <c>take:true</c> moves every
/// binding another project holds for the keys named, each user's root with it; <c>unbind:</c> removes them. Both need
/// only work_update (an orchestrator) and membership of the projects involved; both journal a <c>repo</c> event.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class RepoMoveTests(PostgresFixture fixture)
{
    private const string Root = "p:/projects/example/pa-two";

    [Fact]
    public async Task RepoMove_the_already_bound_refusal_names_the_take_call()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world.ProjectId);
        await OtherAsync(world);

        var refused = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Repo: new RepoArg(Root: Root)), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains($"work_update(ref:\"{project}\", repo:{{root:\"{Root}\"}}, take:true)", refused.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RepoMove_take_without_repo_is_refused()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var project = await ProjectRefAsync(world.ProjectId);

        var refused = await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, project,
            new UpdateArgs(Title: "x", Take: true), Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
    }

    [Fact]
    public async Task RepoMove_take_moves_every_users_binding_and_the_repo_opens_the_new_project_for_each()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await RootsInP2Async(world);
        Assert.Equal(4, await CountAsync(world.P2Id));

        await using var both = await world.McpAsync((await world.CredentialsAsync(world.User2))[1].Mcp!);
        var taken = await ReadServer.CallEitherAsync(both, "work_update", Args(
            ("ref", ProjectWorld.P1),
            ("repo", Repo(ProjectWorld.RemoteP2, Root)),
            ("take", true)), Ct);

        Assert.False(taken.Refused, taken.Text);
        Assert.Contains($"— taken from {ProjectWorld.P2}", taken.Text, StringComparison.Ordinal);

        Assert.Equal(0, await CountAsync(world.P2Id));
        Assert.Equal(
            new[]
            {
                $"remote {RepoKeys.NormaliseRemote(ProjectWorld.RemoteP2)}|{Guid.Empty}",
                $"root {Root}|{Guid.Empty}",
                $"root {Root}|{world.User1Id}",
                $"root {Root}|{world.User2Id}",
            }.Order(),
            (await RowsAsync(world.P1Id)).Order());

        Assert.Equal(["repo"], await EventKindsAsync(world.P2Id));
        Assert.Contains("repo", await EventKindsAsync(world.P1Id));

        // The caller, and a user whose root moved without them calling anything, both open P1 from the repo.
        foreach (var user in new[] { world.User2, world.User1 })
        {
            await using var client = await world.McpAsync((await world.CredentialsAsync(user))[1].Mcp!);
            var brief = await ReadServer.CallAsync(client, "work_brief", Args(("repo", Repo(null, Root))), Ct);
            Assert.StartsWith($"PROJECT {ProjectWorld.P1}", brief.Split('\n')[0], StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task RepoMove_take_is_refused_without_access_to_the_project_that_holds_the_repo()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        var before = await CountAsync(world.P2Id);

        // User1 is a member of P1 only; P2 holds RemoteP2.
        await using var outsider = await world.McpAsync((await world.CredentialsAsync(world.User1))[1].Mcp!);
        var refused = await ReadServer.CallEitherAsync(outsider, "work_update", Args(
            ("ref", ProjectWorld.P1),
            ("repo", Repo(ProjectWorld.RemoteP2, null)),
            ("take", true)), Ct);

        Assert.True(refused.Refused, refused.Text);
        Assert.DoesNotContain(ProjectWorld.TitleP2, refused.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("this repo is project", refused.Text, StringComparison.Ordinal);
        Assert.Equal(before, await CountAsync(world.P2Id));
        Assert.Equal(0, await CountAsync(world.P1Id));
        Assert.Empty(await EventKindsAsync(world.P2Id));
    }

    [Fact]
    public async Task RepoMove_unbind_removes_the_remote_and_every_users_root_and_leaves_a_valid_project()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await RootsInP2Async(world);

        await using var member = await world.McpAsync((await world.CredentialsAsync(world.User2))[1].Mcp!);
        var unbound = await ReadServer.CallEitherAsync(member, "work_update", Args(
            ("ref", ProjectWorld.P2),
            ("unbind", Repo(ProjectWorld.RemoteP2, Root))), Ct);

        Assert.False(unbound.Refused, unbound.Text);
        Assert.Contains("— unbound", unbound.Text, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync(world.P2Id));
        Assert.Equal(["repo"], await EventKindsAsync(world.P2Id));

        var get = await ReadServer.CallAsync(member, "work_get", Args(("refs", new[] { ProjectWorld.P2 })), Ct);
        Assert.DoesNotContain("  repo      ", get, StringComparison.Ordinal);
        var brief = await ReadServer.CallAsync(member, "work_brief", Args(("repo", Repo(null, Root))), Ct);
        Assert.Contains("this repo is not bound to a project", brief, StringComparison.Ordinal);

        var again = await ReadServer.CallEitherAsync(member, "work_update", Args(
            ("ref", ProjectWorld.P2),
            ("unbind", Repo(null, Root))), Ct);
        Assert.False(again.Refused, again.Text);
        Assert.Contains("not this project's, nothing removed", again.Text, StringComparison.Ordinal);
    }

    /// <summary>Besides P2's remote: the root for User1, for User2 and for no user, all the same path.</summary>
    private async Task RootsInP2Async(ProjectWorld world)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        foreach (var user in new Guid?[] { world.User1Id, world.User2Id, null })
        {
            await connection.ExecuteAsync(
                """
                INSERT INTO project_repo (id, workspace_id, project_item_id, user_id, kind, key)
                VALUES (gen_random_uuid(), @workspace, @project, @user, 'root', @key);
                """,
                new { workspace = world.WorkspaceId, project = world.P2Id, user, key = Root });
        }
    }

    private async Task OtherAsync(WriteWorld world)
    {
        var other = await world.Writer.CreateAsync(world.WorkspaceId, WriteSeed.Orchestrator,
            [new NewItemArg("project", "The other repo", Ref: "other", Repo: new RepoArg(Root: Root))], Ct);
        Assert.False(other.Refused, other.Text);
    }

    private async Task<string> ProjectRefAsync(Guid id)
    {
        await using var db = fixture.NewContext();
        return (await db.Items.SingleAsync(i => i.Id == id, Ct)).Ref;
    }

    private async Task<int> CountAsync(Guid project)
    {
        await using var db = fixture.NewContext();
        return await db.ProjectRepos.CountAsync(r => r.ProjectItemId == project, Ct);
    }

    private async Task<List<string>> RowsAsync(Guid project)
    {
        await using var db = fixture.NewContext();
        return [.. (await db.ProjectRepos.Where(r => r.ProjectItemId == project).ToListAsync(Ct))
            .Select(r => $"{r.Kind} {r.Key}|{r.UserId ?? Guid.Empty}")];
    }

    private async Task<List<string>> EventKindsAsync(Guid item)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return [.. await connection.QueryAsync<string>(
            "SELECT kind FROM event WHERE item_id = @item AND kind = 'repo';", new { item })];
    }

    private static Dictionary<string, object?> Repo(string? remote, string? root)
        => new() { ["remote"] = remote, ["root"] = root };

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);
}
