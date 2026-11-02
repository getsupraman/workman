using Dapper;
using Workman.Tests.Read;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.App;

/// <summary>
/// doc 02 §15b against the ref allocators: a ref a non-member cannot see is still taken. <c>C…</c> is numbered across
/// the workspace, so a rule of a project hidden from the caller holds a number the caller's probe cannot read; the live
/// 2.0.0 failure was every work_constrain refused by <c>constraint_ref_unique</c> for that.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class HiddenRefTests(PostgresFixture fixture)
{
    private const string HiddenRule = "quokka rule only P2 binds";

    [Fact]
    public async Task A_rule_of_a_project_hidden_from_the_caller_keeps_its_number_and_the_caller_takes_the_next()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await RuleAsync(world, world.P2Id, "C1", HiddenRule);

        var oauth = (await world.CredentialsAsync(world.User1))[1];
        await using var mcp = await world.McpAsync(oauth.Mcp!);

        var scoped = await ReadServer.CallEitherAsync(mcp, "work_constrain", Args(
            ("kind", "stack_fact"), ("text", "a member's rule"), ("scope", ProjectWorld.P1)), Ct);
        Assert.False(scoped.Refused, scoped.Text);
        Assert.StartsWith("OK  C2 ", scoped.Text, StringComparison.Ordinal);

        var everywhere = await ReadServer.CallEitherAsync(mcp, "work_constrain", Args(
            ("kind", "stack_fact"), ("text", "a workspace rule")), Ct);
        Assert.False(everywhere.Refused, everywhere.Text);
        Assert.StartsWith("OK  C3 ", everywhere.Text, StringComparison.Ordinal);

        Assert.DoesNotContain(HiddenRule, scoped.Text + everywhere.Text, StringComparison.Ordinal);
        Assert.Equal(["C1", "C2", "C3"], await RefsAsync(world));
    }

    [Fact]
    public async Task A_hidden_rule_stays_hidden_from_the_caller_after_the_caller_numbers_past_it()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;
        await RuleAsync(world, world.P2Id, "C1", HiddenRule);

        var oauth = (await world.CredentialsAsync(world.User1))[1];
        await using var mcp = await world.McpAsync(oauth.Mcp!);

        var written = await ReadServer.CallEitherAsync(mcp, "work_constrain", Args(
            ("kind", "stack_fact"), ("text", "a member's rule"), ("scope", ProjectWorld.P1)), Ct);
        Assert.False(written.Refused, written.Text);

        var retire = await ReadServer.CallEitherAsync(mcp, "work_constrain", Args(
            ("ref", "C1"), ("retire", true), ("reason", "not theirs to retire")), Ct);
        Assert.True(retire.Refused, retire.Text);
        Assert.DoesNotContain(HiddenRule, retire.Text, StringComparison.Ordinal);

        var conventions = await ReadServer.CallEitherAsync(mcp, "work_brief", Args(("scope", $"{ProjectWorld.P1}/run-a/1.1")), Ct);
        Assert.DoesNotContain(HiddenRule, conventions.Text, StringComparison.Ordinal);
        Assert.Contains("a member's rule", conventions.Text, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>A…</c> is numbered within a run, and a run belongs to one project, so a hidden project's assumptions never
    /// share the caller's numbering: P2's <c>run-a</c> holding <c>A1</c> leaves <c>A1</c> free in P1's.
    /// </summary>
    [Fact]
    public async Task An_assumption_of_a_hidden_project_shares_no_numbering_with_the_callers_run()
    {
        var world = await ProjectWorld.CreateAsync(fixture);
        await using var server = world.Server;

        await using var owner = await world.McpAsync(ReadServer.ControllerToken);
        var theirs = await ReadServer.CallEitherAsync(owner, "work_assume", Args(
            ("ref", $"{ProjectWorld.P2}/run-a"), ("statement", "quokka assumption of P2"), ("evidence", "asserted"), ("reversal", "low")), Ct);
        Assert.False(theirs.Refused, theirs.Text);
        Assert.Contains("A1", theirs.Text, StringComparison.Ordinal);

        var oauth = (await world.CredentialsAsync(world.User1))[1];
        await using var mcp = await world.McpAsync(oauth.Mcp!);
        var ours = await ReadServer.CallEitherAsync(mcp, "work_assume", Args(
            ("ref", $"{ProjectWorld.P1}/run-a"), ("statement", "a member's assumption"), ("evidence", "asserted"), ("reversal", "low")), Ct);
        Assert.False(ours.Refused, ours.Text);
        Assert.Contains("A1", ours.Text, StringComparison.Ordinal);
    }

    private static async Task RuleAsync(ProjectWorld world, Guid scope, string reference, string text)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            INSERT INTO constraint_rule (id, workspace_id, scope_item_id, ref, kind, text)
            VALUES (@id, @workspace, @scope, @ref, 'stack_fact', @text);
            """,
            new { id = Guid.CreateVersion7(), workspace = world.WorkspaceId, scope, @ref = reference, text });
    }

    private static async Task<List<string>> RefsAsync(ProjectWorld world)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        return [.. await connection.QueryAsync<string>(
            "SELECT ref FROM constraint_rule WHERE workspace_id = @workspace ORDER BY ref;", new { workspace = world.WorkspaceId })];
    }

    private static IReadOnlyDictionary<string, object?> Args(params (string Name, object? Value)[] args)
        => args.ToDictionary(a => a.Name, a => a.Value);
}
