using System.Text.Json;
using Dapper;
using Workman.Core.Domain;
using Workman.Core.Rendering;
using Workman.Tests.App.V1;
using Workman.Tests.Store;
using Workman.Tests.Write;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Read;

/// <summary>
/// mcp-fit/7: Needs you in groups — every inbox row says whether it is a decision, waits on someone, is blocked with no
/// date, or is housekeeping; a row whose not_before has not started is not there; and an open question a decision note
/// already answers gets an attention line that names its close.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class NeedsYouGroupsTests(PostgresFixture fixture)
{
    [Fact]
    public async Task Inbox_puts_each_row_in_its_group_and_a_waiting_row_carries_the_due_it_is_ordered_by()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "Q1", kind: "question");

        var late = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");
        await DueAsync(world, late, "2099-06-01");
        var soon = await WriteSeed.ChildAsync(world, world.PhaseId, "W2", kind: "wait");
        await DueAsync(world, soon, "2099-01-01");
        var held = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Blocked);
        await WriteSeed.LinkAsync(world, late, held, LinkType.Blocks);

        await WriteSeed.ChildAsync(world, world.PhaseId, "1.3", status: ItemStatus.Blocked);
        var dated = await WriteSeed.ChildAsync(world, world.PhaseId, "1.4", status: ItemStatus.Blocked);
        await DueAsync(world, dated, "2099-03-01");
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.5", status: ItemStatus.Done);

        var inbox = await InboxAsync(world);
        var rows = V1Http.Array(inbox, "rows");

        Assert.Equal(
            [
                ("1.2", "blocked", "waiting"), ("1.3", "blocked", "blocked-undated"), ("1.4", "blocked", "decide"),
                ("1.5", "done_unverified", "housekeeping"), ("Q1", "question", "decide"),
                ("W1", "wait", "waiting"), ("W2", "wait", "waiting"),
            ],
            rows.Select(r => (Ref(r), r.GetProperty("kind").GetString(), r.GetProperty("group").GetString()))
                .Order()
                .ToList());

        // The waiting group reads its wait's due: the blocked item the later wait holds sorts with that wait.
        var waiting = rows.Where(r => r.GetProperty("group").GetString() == "waiting")
            .OrderBy(r => r.GetProperty("dueAt").GetDateTimeOffset())
            .ThenBy(Ref, StringComparer.Ordinal)
            .Select(r => (Ref(r), r.GetProperty("due").GetString()))
            .ToList();
        Assert.Equal([("W2", "2099-01-01"), ("1.2", "2099-06-01"), ("W1", "2099-06-01")], waiting);

        var blocked = V1Http.ByRef(rows, "1.2");
        Assert.Equal("human", blocked.GetProperty("audience").GetString());
        Assert.Equal("work_status(ref:\"W1\", to:\"done\") when its answer arrives", blocked.GetProperty("call").GetString());
        Assert.Equal("human", V1Http.ByRef(rows, "W1").GetProperty("audience").GetString());
        Assert.Equal(JsonValueKind.Null, V1Http.ByRef(rows, "1.3").GetProperty("due").ValueKind);
        Assert.Equal(JsonValueKind.Null, V1Http.ByRef(rows, "1.5").GetProperty("due").ValueKind);

        var groups = inbox.GetProperty("groups");
        Assert.Equal(
            (2, 3, 1, 1),
            (groups.GetProperty("decide").GetInt32(), groups.GetProperty("waiting").GetInt32(),
             groups.GetProperty("blockedUndated").GetInt32(), groups.GetProperty("housekeeping").GetInt32()));
    }

    [Fact]
    public async Task A_wait_or_item_whose_not_before_has_not_started_is_not_in_the_inbox_and_is_once_it_has()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var wait = await WriteSeed.ChildAsync(world, world.PhaseId, "W1", kind: "wait");
        var blocked = await WriteSeed.ChildAsync(world, world.PhaseId, "1.2", status: ItemStatus.Blocked);
        var question = await WriteSeed.ChildAsync(world, world.PhaseId, "Q1", kind: "question");
        await NotBeforeAsync(world, [wait, blocked, question], "2099-01-01");

        var hidden = V1Http.Array(await InboxAsync(world), "rows");
        Assert.Empty(hidden);
        Assert.Equal(0, (await InboxAsync(world)).GetProperty("groups").GetProperty("waiting").GetInt32());

        await NotBeforeAsync(world, [wait, blocked, question], "2020-01-01");

        Assert.Equal(["1.2", "Q1", "W1"], V1Http.Array(await InboxAsync(world), "rows").Select(Ref).Order().ToList());
    }

    [Fact]
    public async Task An_open_question_with_a_decision_note_gets_the_line_that_closes_it_and_others_do_not()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.ChildAsync(world, world.PhaseId, "Q1", kind: "question");
        await WriteSeed.ChildAsync(world, world.PhaseId, "Q2", kind: "question");
        await WriteSeed.ChildAsync(world, world.PhaseId, "Q3", kind: "question", status: ItemStatus.Done);
        await WriteSeed.ChildAsync(world, world.PhaseId, "Q4", kind: "question");

        const string decision = "Keep the bare ref in the first column, as the board does";
        Assert.False((await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "decision", decision, null, "Q1", Ct)).Refused);
        Assert.False((await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "decision", "Ship it on the first", null, "Q3", Ct)).Refused);
        Assert.False((await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "observation", "Seen in chat", "Q4", null, Ct)).Refused);
        Assert.False((await world.Writer.NoteAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "decision", "Use the day form", "Q4", null, Ct)).Refused);

        var lines = (await world.Reader.AttentionAsync(await world.ScopeAsync("run-a"), Ct))
            .Where(l => l.Rank == AttentionRank.QuestionAnswered)
            .OrderBy(l => l.Ref, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Q1", "Q4"], lines.Select(l => l.Ref));
        Assert.Equal("Q1 may be answered: decision \"Keep the bare ref i…\"", lines[0].What);
        Assert.Equal("work_status(ref:\"Q1\", to:\"done\", reason:\"…\")", lines[0].Call);
        Assert.Equal("run-a/Q1", lines[0].QualifiedRef);
        Assert.Equal("Q4 may be answered: decision \"Use the day form\"", lines[1].What);

        var block = Attention.Block(lines);
        Assert.All(block.Split('\n'), l => Assert.True(l.Length <= Columns.Width, $"{l.Length} columns: {l}"));
        Assert.Contains("⚠ Q1 may be answered: decision \"", block, StringComparison.Ordinal);

        Assert.True(AttentionRank.AssumptionPastRevisit < AttentionRank.QuestionAnswered);
        Assert.True(AttentionRank.QuestionAnswered < AttentionRank.UnconsumedNote);
    }

    private static string Ref(JsonElement row) => row.GetProperty("item").GetProperty("ref").GetString()!;

    private async Task<JsonElement> InboxAsync(WriteWorld world)
    {
        await using var server = await V1Http.StartAsync(fixture, world.Slug);
        return await V1Http.GetAsync(await V1Http.ReaderAsync(server), $"/api/v1/inbox?project={world.ProjectId}");
    }

    private static Task DueAsync(WriteWorld world, Guid itemId, string day)
        => ExecAsync(world, "UPDATE item SET due_on = @day::date WHERE id = @id;", new { id = itemId, day });

    private static Task NotBeforeAsync(WriteWorld world, Guid[] ids, string day)
        => ExecAsync(world, "UPDATE item SET not_before_on = @day::date WHERE id = ANY(@ids);", new { ids, day });

    private static async Task ExecAsync(WriteWorld world, string sql, object parameters)
    {
        await using var connection = await world.Fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(sql, parameters);
    }
}
