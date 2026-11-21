using Dapper;
using Microsoft.EntityFrameworkCore;
using Workman.Core.Domain;
using Workman.Core.Writing;
using Workman.Tests.Store;
using Xunit;
using static Workman.Tests.Store.Tokens;

namespace Workman.Tests.Write;

/// <summary>
/// mcp-fit/6 — a controller claims an item for the nested session that works it, with a lease up to 480 minutes. The
/// named session holds it, the calls of either session renew it, and it ends with that session or with the
/// controller's next move.
/// </summary>
[Collection(StoreCollection.Name)]
public sealed class DelegatedClaimTests(PostgresFixture fixture)
{
    private const string Worker = "wk-a";

    [Fact]
    public async Task A_controller_claims_for_a_session_with_a_capped_lease_and_the_default_stays_30()
    {
        var (world, controller) = await WorldAsync();
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        var worker = await SessionAsync(world, Worker, ActorRole.Worker);

        var capped = await Start(world, "1.1", controller, lease: 481, forSession: Worker);
        Assert.True(capped.Refused, capped.Text);
        RefusalShape.SelfCorrecting(capped.Text);
        Assert.Contains("1..480", capped.Text, StringComparison.Ordinal);
        Assert.Contains("lease:480", capped.Text, StringComparison.Ordinal);
        Assert.Empty(await ClaimsAsync(world));
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));

        var taken = await Start(world, "1.1", controller, lease: 240, forSession: Worker);
        Assert.False(taken.Refused, taken.Text);
        Assert.Contains($"acquired by {Worker} (for {controller.Session})", taken.Text, StringComparison.Ordinal);

        var claim = Assert.Single(await ClaimsAsync(world));
        Assert.Equal(worker, claim.SessionId);
        Assert.Equal(controller.SessionId, claim.ClaimedBySessionId);
        Assert.Equal(controller.Session, claim.ClaimedBy);
        Assert.Equal(240, claim.LeaseMinutes);
        AssertMinutesLeft(240, claim.ExpiresAt);

        // The worker holds the run's tree now; the controller's own claim names a tree of its own.
        var plain = await world.Writer.StatusAsync(
            world.WorkspaceId, "1.2", "in_progress", controller, new StatusArgs(Tree: "ctl-a-branch"), Ct);
        Assert.False(plain.Refused, plain.Text);
        var own = Assert.Single(await ClaimsAsync(world), c => c.ItemId != world.TaskId);
        Assert.Null(own.LeaseMinutes);
        AssertMinutesLeft(30, own.ExpiresAt);

        var got = await world.Reader.GetAsync(await world.ScopeAsync("1.1"), ["1.1"], [], Ct);
        Assert.Contains($"held by {Worker} (for {controller.Session})", got, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_holder_and_the_claiming_controller_renew_and_an_unrelated_session_does_not()
    {
        var (world, controller) = await WorldAsync();
        await SessionAsync(world, Worker, ActorRole.Worker);
        await SessionAsync(world, "wk-other", ActorRole.Worker);
        Assert.False((await Start(world, "1.1", controller, lease: 240, forSession: Worker)).Refused);

        var sessions = new SessionStore(fixture.DataSource);

        await LapseSoonAsync(world);
        await sessions.ResolveAsync(world.WorkspaceId, ActorRole.Worker, new SessionFacts(Name: "wk-other"), Ct);
        AssertMinutesLeft(1, Assert.Single(await ClaimsAsync(world)).ExpiresAt);

        await sessions.ResolveAsync(world.WorkspaceId, ActorRole.Worker, new SessionFacts(Name: Worker), Ct);
        AssertMinutesLeft(240, Assert.Single(await ClaimsAsync(world)).ExpiresAt);

        await LapseSoonAsync(world);
        await sessions.ResolveAsync(world.WorkspaceId, ActorRole.Controller, new SessionFacts(Name: controller.Session), Ct);
        AssertMinutesLeft(240, Assert.Single(await ClaimsAsync(world)).ExpiresAt);
    }

    [Fact]
    public async Task Releasing_or_revoking_the_named_session_releases_its_delegated_claim()
    {
        var (world, controller) = await WorldAsync();
        await WriteSeed.ChildAsync(world, world.PhaseId, "1.2");
        var worker = await SessionAsync(world, Worker, ActorRole.Worker);
        var second = await SessionAsync(world, "wk-b", ActorRole.Worker);
        var sessions = new SessionStore(fixture.DataSource);

        Assert.False((await Start(world, "1.1", controller, lease: 120, forSession: Worker)).Refused);

        var released = await sessions.ReleaseNameAsync(world.WorkspaceId, worker, controller, Ct);
        Assert.Equal(ReleaseOutcome.Released, released.Outcome);
        Assert.Empty(await ClaimsAsync(world, live: true));
        Assert.Equal("session_released", Assert.Single(await ClaimsAsync(world)).ReleaseReason);
        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.1"));
        Assert.Contains("claim_session_released", await WriteSeed.EventKindsAsync(world, world.TaskId));

        Assert.False((await Start(world, "1.2", controller, forSession: "wk-b")).Refused);
        await sessions.RevokeAsync(second, controller, Ct);
        Assert.Empty(await ClaimsAsync(world, live: true));
        Assert.Equal(ItemStatus.Ready, await world.StatusAsync("1.2"));
    }

    /// <summary>The refusal for a session's own claims stays as it was: only a claim taken for it is let go.</summary>
    [Fact]
    public async Task Releasing_a_session_that_holds_its_own_claim_is_still_refused()
    {
        var (world, _) = await WorldAsync();
        var worker = await SessionAsync(world, Worker, ActorRole.Orchestrator);
        var self = new Actor("orchestrator", ActorRole.Orchestrator) { SessionId = worker, Session = Worker };

        Assert.False((await Start(world, "1.1", self)).Refused);

        var refused = await new SessionStore(fixture.DataSource).ReleaseNameAsync(world.WorkspaceId, worker, self, Ct);
        Assert.Equal(ReleaseOutcome.Holding, refused.Outcome);
        Assert.Single(await ClaimsAsync(world, live: true));
    }

    [Fact]
    public async Task The_controller_moves_the_item_out_and_takes_it_back_as_its_holder()
    {
        var (world, controller) = await WorldAsync();
        await SessionAsync(world, Worker, ActorRole.Worker);
        Assert.False((await Start(world, "1.1", controller, lease: 240, forSession: Worker)).Refused);

        var review = await world.Writer.StatusAsync(world.WorkspaceId, "1.1", "review", controller, new StatusArgs(), Ct);
        Assert.False(review.Refused, review.Text);
        Assert.Empty(await ClaimsAsync(world, live: true));

        var back = await Start(world, "1.1", controller);
        Assert.False(back.Refused, back.Text);

        var claim = Assert.Single(await ClaimsAsync(world, live: true));
        Assert.Equal(controller.SessionId, claim.SessionId);
        Assert.Null(claim.ClaimedBy);
        Assert.Null(claim.LeaseMinutes);
    }

    [Fact]
    public async Task A_controller_takes_back_a_delegated_item_in_place()
    {
        var (world, controller) = await WorldAsync();
        await SessionAsync(world, Worker, ActorRole.Worker);
        Assert.False((await Start(world, "1.1", controller, lease: 240, forSession: Worker)).Refused);

        var back = await Start(world, "1.1", controller);
        Assert.False(back.Refused, back.Text);

        var claim = Assert.Single(await ClaimsAsync(world, live: true));
        Assert.Equal(controller.SessionId, claim.SessionId);
        Assert.Null(claim.ClaimedBy);
        AssertMinutesLeft(30, claim.ExpiresAt);
    }

    [Theory]
    [InlineData(60, null)]
    [InlineData(null, Worker)]
    public async Task A_worker_cannot_set_a_lease_or_claim_for_another_session(int? lease, string? forSession)
    {
        var (world, _) = await WorldAsync();
        var worker = await SessionAsync(world, Worker, ActorRole.Worker);
        await SessionAsync(world, "wk-b", ActorRole.Worker);
        var self = new Actor("worker", ActorRole.Worker) { SessionId = worker, Session = Worker };

        var refused = await Start(world, "1.1", self, lease, forSession is null ? null : "wk-b");
        Assert.True(refused.Refused, refused.Text);
        RefusalShape.SelfCorrecting(refused.Text);
        Assert.Contains("only an orchestrator", refused.Text, StringComparison.Ordinal);
        Assert.Empty(await ClaimsAsync(world));
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));

        // Its own plain claim is as it was.
        Assert.False((await Start(world, "1.1", self)).Refused);
    }

    [Fact]
    public async Task For_session_naming_an_unknown_or_released_session_is_refused()
    {
        var (world, controller) = await WorldAsync();
        var gone = await SessionAsync(world, "wk-gone", ActorRole.Worker);
        await new SessionStore(fixture.DataSource).ReleaseNameAsync(world.WorkspaceId, gone, controller, Ct);

        foreach (var name in new[] { "wk-never", "wk-gone" })
        {
            var refused = await Start(world, "1.1", controller, lease: 60, forSession: name);
            Assert.True(refused.Refused, refused.Text);
            RefusalShape.SelfCorrecting(refused.Text);
            Assert.Contains($"no live session is named \"{name}\"", refused.Text, StringComparison.Ordinal);
        }

        Assert.Empty(await ClaimsAsync(world));
        Assert.Equal(ItemStatus.Open, await world.StatusAsync("1.1"));
    }

    private sealed record ClaimRow(
        Guid ItemId,
        Guid? SessionId,
        Guid? ClaimedBySessionId,
        string? ClaimedBy,
        int? LeaseMinutes,
        DateTimeOffset ExpiresAt,
        string? ReleaseReason);

    private async Task<(WriteWorld World, Actor Controller)> WorldAsync()
    {
        var world = await WriteSeed.CreateAsync(fixture);
        var id = await SessionAsync(world, "ctl-a", ActorRole.Controller);
        return (world, new Actor("controller", ActorRole.Controller) { SessionId = id, Session = "ctl-a" });
    }

    private async Task<Guid> SessionAsync(WriteWorld world, string name, ActorRole role)
    {
        await using var db = fixture.NewContext();
        var session = new AgentSession { Id = Guid.CreateVersion7(), WorkspaceId = world.WorkspaceId, Name = name, Role = role };
        db.AgentSessions.Add(session);
        await db.SaveChangesAsync(Ct);
        return session.Id;
    }

    private static Task<WriteResult> Start(
        WriteWorld world, string reference, Actor actor, int? lease = null, string? forSession = null)
        => world.Writer.StatusAsync(
            world.WorkspaceId, reference, "in_progress", actor, new StatusArgs(Lease: lease, ForSession: forSession), Ct);

    private async Task<IReadOnlyList<ClaimRow>> ClaimsAsync(WriteWorld world, bool live = false)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        return [.. await connection.QueryAsync<ClaimRow>(
            """
            SELECT c.item_id AS "ItemId", c.session_id AS "SessionId", c.claimed_by_session_id AS "ClaimedBySessionId",
                   c.claimed_by AS "ClaimedBy", c.lease_minutes AS "LeaseMinutes", c.expires_at AS "ExpiresAt",
                   c.release_reason AS "ReleaseReason"
            FROM   claim c JOIN item i ON i.id = c.item_id
            WHERE  i.workspace_id = @workspace AND (NOT @live OR c.released_at IS NULL)
            ORDER  BY c.acquired_at;
            """,
            new { workspace = world.WorkspaceId, live })];
    }

    private async Task LapseSoonAsync(WriteWorld world)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync(Ct);
        await connection.ExecuteAsync(
            """
            UPDATE claim c SET expires_at = now() + interval '1 minute'
              FROM item i WHERE i.id = c.item_id AND i.workspace_id = @workspace AND c.released_at IS NULL;
            """,
            new { workspace = world.WorkspaceId });
    }

    private static void AssertMinutesLeft(int minutes, DateTimeOffset expiresAt)
    {
        var left = (expiresAt - DateTimeOffset.UtcNow).TotalMinutes;
        Assert.InRange(left, minutes - 2, minutes + 0.5);
    }
}
