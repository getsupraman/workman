using Dapper;
using Microsoft.EntityFrameworkCore;
using Workman.Core.Domain;
using Workman.Core.Queries;
using Workman.Core.Rendering;
using Workman.Core.Writing;
using Workman.Tests.App;
using Workman.Tests.App.V1;
using Workman.Tests.Read;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// mcp-fit/4 — a confirmation by a person: <c>work_verify(ref, confirmed_by, quote)</c>, or the one-call close's
/// <c>verify:{confirmed_by, quote}</c>. It closes an item only when its <c>verify_cmd</c> is empty or explicitly manual,
/// it is never shown or counted as a passed verification, and a close on it is not forced (C343, C344, C345).
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class ConfirmationTests(PostgresFixture fixture)
{
    private const string Dana = "Dana";
    private const string Quote = "The supplier called back: the order is placed.";
    private const string Shown = "Confirmed by Dana: “The supplier called back: the order is placed.”";

    [Fact]
    public async Task Confirmation_closes_a_task_with_no_verify_cmd_and_the_close_is_not_forced()
    {
        var world = await WorldAsync(verifyCmd: null);

        var confirmed = await Confirm(world);
        Assert.False(confirmed.Refused, confirmed.Text);
        Assert.Contains(Shown, confirmed.Text, StringComparison.Ordinal);

        var closed = await Close(world, new StatusArgs(Commit: "a1b2c3d"));
        Assert.False(closed.Refused, closed.Text);
        Assert.DoesNotContain("FORCED", closed.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));

        var kinds = await WriteSeed.EventKindsAsync(world, world.TaskId);
        Assert.Contains("confirmed", kinds);
        Assert.DoesNotContain("forced_transition", kinds);
        Assert.Equal("status", kinds[^1]);

        var row = await RowAsync(world);
        Assert.Equal(("human", true, (string?)null, Quote, Dana, Quote), row);
    }

    [Fact]
    public async Task Confirmation_closes_a_task_whose_verify_cmd_is_marked_manual_also_through_the_one_call_bundle()
    {
        var world = await WorldAsync(verifyCmd: "Dana checks the delivery note against the order");

        var marked = await world.Writer.UpdateAsync(
            world.WorkspaceId, WriteSeed.Orchestrator, "1.1", new UpdateArgs(VerifyManual: true), Ct);
        Assert.False(marked.Refused, marked.Text);

        var closed = await Close(world, new StatusArgs(
            Commit: "a1b2c3d",
            Verify: new VerifyArg(ConfirmedBy: Dana, Quote: Quote),
            Criteria: [new("c1", Checked: true, Evidence: "delivery note matches")]));

        Assert.False(closed.Refused, closed.Text);
        Assert.Contains(Shown, closed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("✓", closed.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("by orchestrator", closed.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("1.1"));
        Assert.DoesNotContain("forced_transition", await WriteSeed.EventKindsAsync(world, world.TaskId));

        var got = await world.Reader.GetAsync(await world.ScopeAsync("run-a"), ["1.1"], [], Ct);
        Assert.Contains("verify    (manual) Dana checks the delivery note against the order", got, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirmation_on_a_task_with_a_real_verify_cmd_is_refused_with_the_route_and_writes_nothing()
    {
        var world = await WorldAsync(verifyCmd: "dotnet test --filter Guard");
        var before = await TraceAsync(world);

        var refused = await Confirm(world);
        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("work_update(ref:\"1.1\", verify_manual:true)", refused.Text, StringComparison.Ordinal);
        Assert.Contains("command:\"dotnet test --filter Guard\"", refused.Text, StringComparison.Ordinal);

        var bundled = await Close(world, new StatusArgs(Commit: "a1b2c3d", Verify: new VerifyArg(ConfirmedBy: Dana, Quote: Quote)));
        Assert.True(bundled.Refused, bundled.Text);
        Assert.Contains("verify_manual:true", bundled.Text, StringComparison.Ordinal);

        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
        Assert.Equal(before, await TraceAsync(world));
    }

    /// <summary>
    /// The guard reads the marker at close time, not only the write: a confirmation recorded while the check was manual
    /// does not close the item once the marker is taken off its real command.
    /// </summary>
    [Fact]
    public async Task Confirmation_does_not_satisfy_the_guard_once_the_verify_cmd_is_no_longer_manual()
    {
        var world = await WorldAsync(verifyCmd: "dotnet test --filter Guard");
        await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, "1.1", new UpdateArgs(VerifyManual: true), Ct);
        Assert.False((await Confirm(world)).Refused);
        await world.Writer.UpdateAsync(world.WorkspaceId, WriteSeed.Orchestrator, "1.1", new UpdateArgs(VerifyManual: false), Ct);

        var refused = await Close(world, new StatusArgs(Commit: "a1b2c3d"));

        Assert.True(refused.Refused, refused.Text);
        Assert.Contains("no verification with run_by=orchestrator", refused.Text, StringComparison.Ordinal);
        Assert.Equal(ItemStatus.InProgress, await world.StatusAsync("1.1"));
    }

    [Theory]
    [InlineData(null, Quote)]
    [InlineData(Dana, null)]
    [InlineData(" ", Quote)]
    public async Task Confirmation_without_a_person_or_without_the_quote_is_refused(string? person, string? quote)
    {
        var world = await WorldAsync(verifyCmd: null);
        var before = await TraceAsync(world);

        var refused = await world.Writer.ConfirmAsync(world.WorkspaceId, "1.1", WriteSeed.Orchestrator, person, quote, cancellationToken: Ct);
        var bundled = await Close(world, new StatusArgs(Commit: "a1b2c3d", Verify: new VerifyArg(ConfirmedBy: person, Quote: quote)));

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains(person is { Length: > 0 } && !string.IsNullOrWhiteSpace(person) ? "quote is missing" : "confirmed_by is missing", refused.Text, StringComparison.Ordinal);
        Assert.True(bundled.Refused, bundled.Text);
        Assert.Equal(before, await TraceAsync(world));
    }

    [Theory]
    [InlineData("command")]
    [InlineData("result_line")]
    [InlineData("passed")]
    public async Task Confirmation_mixed_with_a_command_run_is_refused(string mixed)
    {
        var world = await WorldAsync(verifyCmd: null);
        var before = await TraceAsync(world);

        var refused = await world.Writer.ConfirmAsync(
            world.WorkspaceId, "1.1", WriteSeed.Orchestrator, Dana, Quote,
            command: mixed == "command" ? "make check" : null,
            resultLine: mixed == "result_line" ? "Passed! 3/3" : null,
            passedGiven: mixed == "passed",
            cancellationToken: Ct);

        var arg = new VerifyArg(
            Command: mixed == "command" ? "make check" : null,
            ResultLine: mixed == "result_line" ? "Passed! 3/3" : null,
            Passed: mixed == "passed" ? true : null,
            ConfirmedBy: Dana,
            Quote: Quote);
        var bundled = await Close(world, new StatusArgs(Commit: "a1b2c3d", Verify: arg));

        Assert.True(refused.Refused, refused.Text);
        Assert.Contains($"a confirmation carries no {mixed}", refused.Text, StringComparison.Ordinal);
        Assert.True(bundled.Refused, bundled.Text);
        Assert.Contains($"a confirmation carries no {mixed}", bundled.Text, StringComparison.Ordinal);
        Assert.Equal(before, await TraceAsync(world));
    }

    [Fact]
    public async Task Confirmation_from_a_worker_is_refused_and_writes_nothing()
    {
        var world = await WorldAsync(verifyCmd: null);
        var before = await TraceAsync(world);

        var refused = await world.Writer.ConfirmAsync(world.WorkspaceId, "1.1", WriteSeed.Worker, Dana, Quote, cancellationToken: Ct);

        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("a worker cannot record a confirmation", refused.Text, StringComparison.Ordinal);
        Assert.Equal(before, await TraceAsync(world));
    }

    [Fact]
    public async Task Confirmation_reads_as_confirmed_by_the_person_in_work_get_the_brief_and_history_never_as_passed()
    {
        var world = await WorldAsync(verifyCmd: null);
        Assert.False((await Confirm(world)).Refused);
        Assert.False((await Close(world, new StatusArgs(Commit: "a1b2c3d"))).Refused);

        var got = await world.Reader.GetAsync(await world.ScopeAsync("run-a"), ["1.1"], [], Ct);
        var evidence = Assert.Single(got.Split('\n'), l => l.StartsWith("  evidence", StringComparison.Ordinal));
        Assert.Contains(Shown, evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("✓", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("by human", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("⚠ no verify", got, StringComparison.Ordinal);

        var brief = await world.Reader.BriefAsync(await world.ScopeAsync("1.1"), "resume", Ct);
        var last = Assert.Single(brief.Split('\n'), l => l.StartsWith("LAST VERIFY", StringComparison.Ordinal));
        Assert.Contains(Shown, last, StringComparison.Ordinal);
        Assert.DoesNotContain("by human", last, StringComparison.Ordinal);

        var history = await world.Reader.HistoryAsync(world.WorkspaceId, await world.ScopeAsync("1.1"), 0, 20, Ct);
        Assert.Single(history.Split('\n'), l => l.Contains(" confirmed ", StringComparison.Ordinal));
        Assert.Contains($"1.1 {Shown}", Unwrapped(history), StringComparison.Ordinal);
        Assert.DoesNotContain("green", history, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirmation_is_counted_on_its_own_never_as_verified_unverified_or_forced_and_work_audit_lists_it_apart()
    {
        var world = await WorldAsync(verifyCmd: null);
        Assert.False((await Confirm(world)).Refused);
        Assert.False((await Close(world, new StatusArgs(Commit: "a1b2c3d"))).Refused);

        var board = new BoardQueries(fixture.DataSource);
        var confirmed = Assert.Single(await board.DoneConfirmedAsync(world.RunId, Ct));
        Assert.Equal((world.TaskId, Dana, Quote), (confirmed.Id, confirmed.ConfirmedBy, confirmed.Quote));
        Assert.DoesNotContain(await board.DoneUnverifiedAsync(world.RunId, Ct), u => u.Id == world.TaskId);
        Assert.DoesNotContain(await board.ForcedTransitionsAsync(world.RunId, Ct), f => f.Ref == "1.1");

        var facts = Assert.Single(await board.FactsAsync([world.TaskId], Ct));
        Assert.Equal((0, 0, 1), (facts.VerificationsPassed, facts.OrchestratorVerifications, facts.Confirmations));

        var audit = await world.Reader.AuditAsync(await world.ScopeAsync("run-a"), Ct);
        Assert.Contains("done-confirmed (1)", audit, StringComparison.Ordinal);
        Assert.Contains(Shown, Unwrapped(audit), StringComparison.Ordinal);
        Assert.DoesNotContain("done-unverified", audit, StringComparison.Ordinal);
        Assert.DoesNotContain("forced-closure", audit, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Confirmation_reaches_the_read_api_as_its_own_evidence_flag_and_honesty_count()
    {
        var world = await WorldAsync(verifyCmd: null);
        Assert.False((await Confirm(world)).Refused);
        Assert.False((await Close(world, new StatusArgs(Commit: "a1b2c3d"))).Refused);

        await using var server = await AppServer.StartAsync(fixture.ConnectionString, world.Slug, Ct);
        var http = V1Http.Bearer(server, ReadServer.ControllerToken);

        var honesty = (await V1Http.GetAsync(http, "/api/v1/overview")).GetProperty("honesty");
        Assert.Equal((1, 0, 0), (honesty.GetProperty("confirmed").GetInt32(), honesty.GetProperty("doneUnverified").GetInt32(),
            honesty.GetProperty("forced").GetInt32()));

        var page = await V1Http.GetAsync(http, $"/api/v1/items/{world.TaskId}");
        var evidence = Assert.Single(page.GetProperty("verifications").EnumerateArray());
        Assert.Equal(Dana, evidence.GetProperty("confirmedBy").GetString());
        Assert.Equal(Quote, evidence.GetProperty("quote").GetString());
        Assert.False(page.GetProperty("verifyManual").GetBoolean());

        var rows = (await V1Http.GetAsync(http, $"/api/v1/runs/{world.RunId}/board")).GetProperty("rows").EnumerateArray();
        var row = Assert.Single(rows, r => r.GetProperty("item").GetProperty("ref").GetString() == "1.1");
        var flags = row.GetProperty("flags").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("confirmed", flags);
        Assert.DoesNotContain("done_unverified", flags);
        Assert.Equal(Dana, row.GetProperty("verification").GetProperty("confirmedBy").GetString());
    }

    [Fact]
    public async Task Confirmation_leaves_a_question_answer_as_it_was_a_human_verification_with_no_person()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var question = await WriteSeed.ChildAsync(world, world.PhaseId, "Q1", kind: "question", status: ItemStatus.InProgress);

        var closed = await world.Writer.StatusAsync(
            world.WorkspaceId, "Q1", "done", WriteSeed.Orchestrator, new StatusArgs(Reason: "Keep the bare ref"), Ct);

        Assert.False(closed.Refused, closed.Text);
        Assert.Equal(ItemStatus.Done, await world.StatusAsync("Q1"));
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        var person = await connection.ExecuteScalarAsync<string?>(
            "SELECT confirmed_by FROM verification WHERE item_id = @id", new { id = question });
        Assert.Null(person);
        Assert.DoesNotContain("Confirmed by", await world.Reader.GetAsync(await world.ScopeAsync("run-a"), ["Q1"], [], Ct), StringComparison.Ordinal);
    }

    [Fact]
    public void Confirmation_satisfies_condition_2_only_on_an_empty_or_manual_verify_cmd()
    {
        VerificationFact[] confirmation = [new(true, null, VerifyRunner.Human, Dana)];

        static DoneFacts Facts(string? cmd, bool manual, IReadOnlyList<VerificationFact> v)
            => new("1.1", "task", cmd, RequiresVerify: true, RequiresCommit: false, HasCommit: false, [], v, [], [], VerifyManual: manual);

        Assert.Empty(DoneGuard.Evaluate(Facts(null, false, confirmation)));
        Assert.Empty(DoneGuard.Evaluate(Facts("Dana checks it", true, confirmation)));
        Assert.Single(DoneGuard.Evaluate(Facts("dotnet test --filter Guard", false, confirmation)));

        // A question answer is a human row with no person: it never stands for condition 2 on a task.
        Assert.Single(DoneGuard.Evaluate(Facts(null, false, [new(true, null, VerifyRunner.Human)])));
    }

    /// <summary>A view's text with its wrapping undone, so a sentence split across lines reads whole.</summary>
    private static string Unwrapped(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ");

    /// <summary>Task 1.1 in progress, one ticked criterion, with <paramref name="verifyCmd"/>.</summary>
    private async Task<WriteWorld> WorldAsync(string? verifyCmd)
    {
        var world = await WriteSeed.CreateAsync(fixture);
        await WriteSeed.CriterionAsync(world, world.TaskId, 1, "the order is placed", ticked: true);

        await using var db = fixture.NewContext();
        var item = await db.Items.FirstAsync(i => i.Id == world.TaskId, Ct);
        item.VerifyCmd = verifyCmd;
        item.Status = ItemStatus.InProgress;
        item.StartedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(Ct);

        return world;
    }

    private static Task<WriteResult> Confirm(WriteWorld world)
        => world.Writer.ConfirmAsync(world.WorkspaceId, "1.1", WriteSeed.Orchestrator, Dana, Quote, cancellationToken: Ct);

    private static Task<WriteResult> Close(WriteWorld world, StatusArgs args)
        => world.Writer.StatusAsync(world.WorkspaceId, "1.1", "done", WriteSeed.Orchestrator, args, Ct);

    private async Task<(string RunBy, bool Passed, string? Command, string ResultLine, string? Person, string? Quote)> RowAsync(WriteWorld world)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return await connection.QuerySingleAsync<(string, bool, string?, string, string?, string?)>(
            "SELECT run_by::text, passed, command, result_line, confirmed_by, quote FROM verification WHERE item_id = @id",
            new { id = world.TaskId });
    }

    /// <summary>Everything a confirmation or a close can write on the item, as one comparable value.</summary>
    private async Task<string> TraceAsync(WriteWorld world)
    {
        await using var db = fixture.NewContext();
        var verifications = await db.Verifications.CountAsync(v => v.ItemId == world.TaskId, Ct);
        var events = await db.Events.CountAsync(e => e.ItemId == world.TaskId, Ct);
        var item = await db.Items.FirstAsync(i => i.Id == world.TaskId, Ct);
        return $"v={verifications} e={events} s={item.Status} r={item.Revision}";
    }
}
