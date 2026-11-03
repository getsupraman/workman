using System.Text.RegularExpressions;
using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Read;

/// <summary>
/// Links are workspace-wide, so a blocker can sit in another project. Here task <c>1.2</c> in
/// project A's <c>run-a</c> waits on a <c>1.1</c> in project B, and project A's <c>run-a</c> holds a
/// <c>1.1</c> of its own: the bare blocker ref names the wrong item, so every rendering must print
/// one that resolves to the blocker alone.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed partial class CrossRunBlockedTests(PostgresFixture fixture)
{
    [Fact]
    public async Task A_blocker_in_another_project_prints_a_ref_that_resolves_to_it_alone()
    {
        var (world, blockerId, _) = await WorldAsync();
        var scope = await world.ScopeAsync("run-a");
        var reader = world.Reader;

        var get = await reader.GetAsync(scope, ["1.2"], [], Ct);
        var tree = await reader.TreeAsync(scope, 5, [], true, Ct);
        var threads = await reader.ThreadsAsync(scope, ["deps"], Ct);

        var getRef = BlockedFlag(Row(get, "1.2"));
        var treeRef = BlockedFlag(Row(tree, "1.2"));
        var depLine = Row(threads, "1.2");
        var depRef = WaitingOn().Match(depLine) is { Success: true } m
            ? m.Groups[1].Value
            : throw new Xunit.Sdk.XunitException($"no \"waiting on\" in: {depLine}");

        foreach (var printed in new[] { getRef, treeRef, depRef })
        {
            await AssertResolvesToAloneAsync(world, printed, blockerId);
        }

        Assert.Contains($"work_status(ref:\"{depRef}\", to:\"done\")", depLine, StringComparison.Ordinal);

        var audit = await reader.AuditAsync(scope, Ct);
        Assert.Contains($"work_move(ref:\"{depRef}\", before:\"1.2\")", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blocker_in_the_same_run_still_prints_bare()
    {
        var (world, _, _) = await WorldAsync();
        var scope = await world.ScopeAsync("run-a");

        var tree = await world.Reader.TreeAsync(scope, 5, [], true, Ct);

        Assert.Equal("1.1", BlockedFlag(Row(tree, "1.3")));
    }

    private static async Task AssertResolvesToAloneAsync(WriteWorld world, string printed, Guid blockerId)
    {
        var matches = await new BoardQueries(world.Fixture.DataSource).ResolveAsync(world.WorkspaceId, printed, Ct);
        Assert.True(
            matches is [var only] && only.Id == blockerId,
            $"\"{printed}\" resolves to {matches.Count} item(s), not to the blocker alone");
    }

    private static string Row(string text, string reference)
        => text.Split('\n').FirstOrDefault(l => l.TrimStart().StartsWith(reference + " ", StringComparison.Ordinal))
           ?? throw new Xunit.Sdk.XunitException($"no row for {reference} in:\n{text}");

    private static string BlockedFlag(string row)
        => BlockedBy().Match(row) is { Success: true } m
            ? m.Groups[1].Value
            : throw new Xunit.Sdk.XunitException($"no blocked-by flag in: {row}");

    [GeneratedRegex(@"← (\S+)")]
    private static partial Regex BlockedBy();

    [GeneratedRegex(@"waiting on (\S+) \(")]
    private static partial Regex WaitingOn();

    /// <summary>Project A's run-a: 1.1, 1.2 (blocked by B's 1.1), 1.3 (blocked by A's 1.1).</summary>
    private async Task<(WriteWorld World, Guid BlockerId, Guid WaiterId)> WorldAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var waiter = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        var sameRunWaiter = await WriteSeed.ChildAsync(world, world.PhaseId, "1.3");

        Guid blockerId;
        await using (var db = fixture.NewContext())
        {
            var project = Seed.NewItem(world.WorkspaceId, null, "project", $"o{Guid.CreateVersion7():n}"[..6], 2m);
            var run = Seed.NewItem(world.WorkspaceId, project.Id, "run", "rb", 1m);
            db.Items.AddRange(project, run);
            await db.SaveChangesAsync(Ct);

            var phase = Seed.NewItem(world.WorkspaceId, run.Id, "phase", "1", 1m);
            db.Items.Add(phase);
            await db.SaveChangesAsync(Ct);

            // Sequenced after its waiter, so work_threads(include:"deps") reports the edge as inverted.
            var blocker = Seed.NewItem(world.WorkspaceId, phase.Id, "task", "1.1", 99m);
            db.Items.Add(blocker);
            await db.SaveChangesAsync(Ct);
            blockerId = blocker.Id;
        }

        await WriteSeed.LinkAsync(world, blockerId, waiter, LinkType.Blocks);
        await WriteSeed.LinkAsync(world, world.TaskId, sameRunWaiter, LinkType.Blocks);

        return (world, blockerId, waiter);
    }
}
