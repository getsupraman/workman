using Dapper;
using Microsoft.EntityFrameworkCore;
using Workman.Core.Domain;
using Workman.Core.Rendering;
using Workman.Tests.App;
using Workman.Tests.App.V1;
using Workman.Tests.Read;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// mcp-fit/5: less noise. A note is acknowledged without being consumed (C346); the never-red audit skips an
/// explicitly manual check (C345); work_tree's rollup and the read API's say what they leave out.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class LessNoiseTests(PostgresFixture fixture)
{
    private const string Body = "the fixture lives under tests/data";

    [Fact]
    public async Task An_acknowledged_note_leaves_attention_and_the_inbox_and_still_reaches_the_next_worker_brief()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.False((await world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", Body, "1.1", null, Ct)).Refused);

        var before = Assert.Single(await NoteLinesAsync(world));
        Assert.Equal("work_note(acknowledge:\"1.1\")", before.Or);
        Assert.Contains(
            "work_brief(scope:\"1.1\", for:\"worker\")\n  or work_note(acknowledge:\"1.1\")",
            Attention.Block([before]),
            StringComparison.Ordinal);

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var http = V1Http.Bearer(server, ReadServer.ControllerToken);
        Assert.Contains(await InboxKindsAsync(http, world), k => k == "unconsumed_note");
        Assert.Contains("unconsumed_notes", await FlagsAsync(http, world));

        var acknowledged = await world.Writer.AcknowledgeNotesAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", null, null, null, Ct);
        Assert.False(acknowledged.Refused, acknowledged.Text);
        Assert.Equal("OK  1.1  1 note(s) acknowledged — they still reach the next worker brief of 1.1", acknowledged.Text);

        Assert.Empty(await NoteLinesAsync(world));
        Assert.DoesNotContain(await InboxKindsAsync(http, world), k => k == "unconsumed_note");
        Assert.DoesNotContain("unconsumed_notes", await FlagsAsync(http, world));

        await using (var db = fixture.NewContext())
        {
            Assert.Null((await db.Notes.SingleAsync(n => n.ToItemId == world.TaskId, Ct)).ConsumedAt);
        }

        Assert.Equal(("orchestrator-a", true), await AcknowledgedAsync(world));

        var brief = await world.Reader.BriefSessionAsync(
            await world.ScopeAsync("1.1"), "worker", WriteSeed.Keeper(fixture), Ct, signed: true);
        Assert.Contains(Body, brief, StringComparison.Ordinal);

        await using (var db = fixture.NewContext())
        {
            Assert.NotNull((await db.Notes.SingleAsync(n => n.ToItemId == world.TaskId, Ct)).ConsumedAt);
        }

        var again = await world.Writer.AcknowledgeNotesAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", null, null, null, Ct);
        Assert.Equal("OK  1.1  0 note(s) acknowledged — they still reach the next worker brief of 1.1", again.Text);
    }

    [Fact]
    public async Task A_worker_cannot_acknowledge_and_acknowledge_mixed_with_a_note_is_refused_writing_nothing()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        Assert.False((await world.Writer.NoteAsync(world.WorkspaceId, WriteSeed.Orchestrator, "carry_forward", Body, "1.1", null, Ct)).Refused);

        var worker = await world.Writer.AcknowledgeNotesAsync(world.WorkspaceId, WriteSeed.Worker, "1.1", null, null, null, Ct);
        Assert.True(worker.Refused);
        Assert.Contains("a worker cannot acknowledge notes. Nothing was written.", worker.Text, StringComparison.Ordinal);

        var withKind = await world.Writer.AcknowledgeNotesAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", "carry_forward", null, null, Ct);
        var withBody = await world.Writer.AcknowledgeNotesAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", null, "a new note", null, Ct);
        Assert.True(withKind.Refused);
        Assert.True(withBody.Refused);
        Assert.Contains("with kind, body or to. Nothing was written.", withBody.Text, StringComparison.Ordinal);

        Assert.Equal((null, false), await AcknowledgedAsync(world));
        Assert.Single(await NoteLinesAsync(world));
        await using (var db = fixture.NewContext())
        {
            Assert.Equal(1, await db.Notes.CountAsync(n => n.WorkspaceId == world.WorkspaceId, Ct));
        }

        // Through the tool: a worker's token is refused the same way, and the orchestrator's acknowledge needs no kind or body.
        await using var server = await ReadServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        await using var asWorker = await server.ConnectAsync(ReadServer.WorkerToken, Ct);
        var (text, refused) = await ReadServer.CallEitherAsync(
            asWorker, "work_note", new Dictionary<string, object?> { ["acknowledge"] = "1.1" }, Ct);
        Assert.True(refused, text);
        Assert.Equal((null, false), await AcknowledgedAsync(world));

        await using var asOrchestrator = await server.ConnectAsync(ReadServer.OrchestratorToken, Ct);
        var (mixed, mixedRefused) = await ReadServer.CallEitherAsync(
            asOrchestrator, "work_note",
            new Dictionary<string, object?> { ["acknowledge"] = "1.1", ["kind"] = "decision", ["body"] = "x" }, Ct);
        Assert.True(mixedRefused, mixed);

        var ok = await ReadServer.CallAsync(
            asOrchestrator, "work_note", new Dictionary<string, object?> { ["acknowledge"] = "1.1" }, Ct);
        Assert.StartsWith("OK  1.1  1 note(s) acknowledged", ok, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_never_red_audit_skips_a_manual_check_and_still_flags_the_same_item_otherwise()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var other = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        await SqlAsync("UPDATE item SET verify_cmd = 'dotnet test --filter Other' WHERE id = @id", other);

        var audit = await world.Reader.AuditAsync(await world.ScopeAsync("run-a"), Ct);
        Assert.Contains("never been recorded red: dotnet test --filter Guard", audit, StringComparison.Ordinal);
        Assert.Contains("never been recorded red: dotnet test --filter Other", audit, StringComparison.Ordinal);

        await SqlAsync("UPDATE item SET verify_manual = true WHERE id = @id", world.TaskId);

        audit = await world.Reader.AuditAsync(await world.ScopeAsync("run-a"), Ct);
        Assert.DoesNotContain("never been recorded red: dotnet test --filter Guard", audit, StringComparison.Ordinal);
        Assert.Contains("never been recorded red: dotnet test --filter Other", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Work_tree_and_the_api_name_the_open_waits_and_findings_their_rollup_leaves_out()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var tree = (await world.Reader.TreeAsync(await world.ScopeAsync("run-a"), 5, [], true, Ct)).Split('\n');
        Assert.EndsWith("0/2 done", Row(tree, "run-a").TrimEnd(), StringComparison.Ordinal);
        Assert.DoesNotContain(tree, l => l.Contains("not counted", StringComparison.Ordinal));

        await WriteSeed.ChildAsync(world, world.PhaseId, "W-1", kind: "wait");
        await WriteSeed.ChildAsync(world, world.PhaseId, "W-2", kind: "wait");
        await WriteSeed.ChildAsync(world, world.PhaseId, "W-3", kind: "wait", status: ItemStatus.Done);
        await WriteSeed.ChildAsync(world, world.RunId, "F-1", kind: "finding");
        await WriteSeed.ChildAsync(world, world.RunId, "F-2", kind: "finding", status: ItemStatus.Cancelled);

        tree = (await world.Reader.TreeAsync(await world.ScopeAsync("run-a"), 5, [], true, Ct)).Split('\n');
        Assert.EndsWith("0/2 done · not counted: 2 open waits, 1 open finding", Row(tree, "run-a").TrimEnd(), StringComparison.Ordinal);
        Assert.EndsWith("0/1 done · not counted: 2 open waits", Row(tree, "1").TrimEnd(), StringComparison.Ordinal);

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var http = V1Http.Bearer(server, ReadServer.ControllerToken);
        var rollup = (await V1Http.GetAsync(http, $"/api/v1/runs/{world.RunId}/board")).GetProperty("rollup");
        Assert.Equal((0, 2), (rollup.GetProperty("done").GetInt32(), rollup.GetProperty("total").GetInt32()));
        var notCounted = rollup.GetProperty("notCounted");
        Assert.Equal((2, 1), (notCounted.GetProperty("waits").GetInt32(), notCounted.GetProperty("findings").GetInt32()));
    }

    private async Task<List<AttentionLine>> NoteLinesAsync(WriteWorld world)
        => [.. (await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct)).Where(l => l.Rank == AttentionRank.UnconsumedNote)];

    private static async Task<List<string?>> InboxKindsAsync(HttpClient http, WriteWorld world)
        => [.. V1Http.Array(await V1Http.GetAsync(http, $"/api/v1/inbox?project={world.ProjectId}"), "rows")
            .Select(r => r.GetProperty("kind").GetString())];

    private static async Task<List<string?>> FlagsAsync(HttpClient http, WriteWorld world)
        => [.. V1Http.ByRef(V1Http.Array(await V1Http.GetAsync(http, $"/api/v1/runs/{world.RunId}/board"), "rows"), "1.1")
            .GetProperty("flags").EnumerateArray().Select(f => f.GetString())];

    private async Task<(string? By, bool At)> AcknowledgedAsync(WriteWorld world)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        var row = await connection.QuerySingleAsync<(string? By, DateTimeOffset? At)>(
            "SELECT acknowledged_by, acknowledged_at FROM note WHERE to_item_id = @id", new { id = world.TaskId });
        return (row.By, row.At is not null);
    }

    private async Task SqlAsync(string sql, Guid id)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(sql, new { id });
    }

    private static string Row(string[] tree, string reference)
        => tree.Single(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [var first, ..] && first == reference);
}
