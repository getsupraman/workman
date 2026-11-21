using System.Text.Json;

namespace Workman.Api.App.V1;

// doc 10 — the frozen v1 shapes, one record per object in the spec. Field names are the spec's,
// camelCased by the host's web JSON defaults. Additive only: never rename or remove a member here.

/// <summary><see cref="FullRef"/> is <c>project/run/ref</c> (a run's is <c>project/run</c>, a project's its ref):
/// longer than <see cref="QualifiedRef"/>, and it stays unambiguous as runs are added.</summary>
public sealed record ItemRefView(Guid Id, string Ref, string QualifiedRef, string Kind, string Title, string Status, string FullRef);

public sealed record RollupView(
    int Total,
    int Done,
    int InProgress,
    int Review,
    int Blocked,
    int Open,
    int Cancelled,
    int Superseded,
    int FindingsOpen,
    NotCountedView NotCounted);

/// <summary>mcp-fit/5: what is open below the item but outside <see cref="RollupView.Total"/> — the rollup says what it leaves out.</summary>
public sealed record NotCountedView(int Waits, int Findings);

public sealed record SessionView(
    Guid Id,
    string Name,
    string Role,
    string? ExternalRole,
    string? Origin,
    string? LaunchedBy,
    string? ClaudeSessionId,
    string? Cwd,
    string? Tree,
    string? Client,
    DateTimeOffset FirstSeenAt,
    DateTimeOffset LastSeenAt);

public sealed record ClaimView(
    ItemRefView Item,
    ItemRefView? Run,
    SessionView? Session,
    string Agent,
    string Role,
    string? Tree,
    DateTimeOffset AcquiredAt,
    DateTimeOffset HeartbeatAt,
    DateTimeOffset ExpiresAt,
    bool Expired,
    string? HeldBySession,
    string? ClaimedBy);

public sealed record JournalEventView(
    long Seq,
    DateTimeOffset At,
    ItemRefView? Item,
    ItemRefView? Run,
    SessionView? Session,
    string Actor,
    string Role,
    string Kind,
    string? Summary,
    JsonElement Payload);

public sealed record LastVerificationView(bool Passed, DateTimeOffset At, bool Confirmed = false);

public sealed record RunOverviewView(
    ItemRefView Item,
    RollupView Rollup,
    string? Branch,
    string? Worktree,
    int ActiveClaims,
    DateTimeOffset? LastEventAt,
    int InboxCount,
    LastVerificationView? LastVerification);

public sealed record RepoView(string Kind, string Key);

/// <summary>Who shares a project (doc 10): <c>Members</c> 0 is open to the workspace; <c>You</c>, the caller is one of them.</summary>
public sealed record SharingView(int Members, bool You, IReadOnlyList<string> Names);

public sealed record ProjectOverviewView(
    ItemRefView Item,
    RollupView Rollup,
    IReadOnlyList<RunOverviewView> Runs,
    IReadOnlyList<RepoView> Repos,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? LastActivityAt,
    int OpenRuns,
    SharingView Sharing);

public sealed record OverviewView(
    DateTimeOffset AsOf,
    long Seq,
    IReadOnlyList<ProjectOverviewView> Projects,
    IReadOnlyList<ClaimView> WorkingNow,
    int InboxCount,
    HonestyView Honesty);

public sealed record CriteriaCountView(int Checked, int Total);

// deadlines/6: Due and NotBefore as written (a day YYYY-MM-DD, or an instant in UTC); Late, closed as done after its
// due; Slips, the times its due moved later. An item that never carried a date reads null, null, false, 0.

/// <param name="ConfirmedBy">Set when the latest evidence is a person's confirmation, never a passed run (C343).</param>
public sealed record VerificationSummaryView(bool Passed, string RunBy, DateTimeOffset At, string? ConfirmedBy = null);

public sealed record BoardRowView(
    ItemRefView Item,
    Guid? ParentId,
    int Depth,
    string Seq,
    bool HasChildren,
    string? Resolution,
    short Priority,
    string? Severity,
    CriteriaCountView Criteria,
    RollupView? Rollup,
    ClaimView? Claim,
    string? Commit,
    VerificationSummaryView? Verification,
    IReadOnlyList<ItemRefView> BlockedBy,
    IReadOnlyList<string> Flags,
    DateTimeOffset OpenedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ClosedAt,
    string? Due = null,
    string? NotBefore = null,
    bool Late = false,
    int Slips = 0,
    bool VerifyManual = false);

public sealed record BoardView(
    DateTimeOffset AsOf,
    long Seq,
    ItemRefView Run,
    ItemRefView? Project,
    string? Intent,
    string? Branch,
    string? Worktree,
    RollupView Rollup,
    IReadOnlyList<BoardRowView> Rows);

public sealed record BoardsView(DateTimeOffset AsOf, long Seq, IReadOnlyList<BoardView> Boards);

/// <summary>
/// <c>/bars</c>: per run, what its bar draws. <see cref="RunBarsView.Leaves"/> are the counted items in pre-order, gone
/// ones included, each with the inputs of the dashboard's <c>state()</c>; <see cref="RunBarsView.Holder"/> is the first
/// row in pre-order that holds a claim.
/// </summary>
public sealed record BarsView(DateTimeOffset AsOf, long Seq, IReadOnlyList<RunBarsView> Runs);

public sealed record RunBarsView(Guid Run, DateTimeOffset? ClosedAt, BarHolderView? Holder, IReadOnlyList<BarLeafView> Leaves);

/// <summary>The claimed item, the holding session's name (null without one) and the claim's role.</summary>
public sealed record BarHolderView(ItemRefView Item, string? Session, string Role);

/// <summary><see cref="Proof"/>: the latest verification passed and was not the worker's own; <see cref="WaitingOn"/>: the
/// qualified refs of the unclosed items that block it.</summary>
public sealed record BarLeafView(
    Guid Id,
    string QualifiedRef,
    string Title,
    string Kind,
    string Status,
    string? Resolution,
    bool ClaimExpired,
    bool Proof,
    IReadOnlyList<string> WaitingOn,
    bool Confirmed = false);

public sealed record CriterionView(string Id, string Text, bool Checked, string? Evidence, DateTimeOffset? CheckedAt);

public sealed record ReportView(
    int Attempt,
    SessionView? Session,
    string? Agent,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    string Status,
    IReadOnlyList<string> Files,
    string? Verify,
    string? RedCheck,
    string? Deviations,
    string? Risks,
    string? NotDone,
    DateTimeOffset At);

public sealed record VerificationView(
    string? Command,
    string ResultLine,
    bool Passed,
    JsonElement? Counts,
    string RunBy,
    SessionView? Session,
    string? RedCheck,
    DateTimeOffset At,
    string? ConfirmedBy = null,
    string? Quote = null);

public sealed record ArtifactView(string Kind, string Value, DateTimeOffset At);

public sealed record ChildView(ItemRefView Item, CriteriaCountView Criteria);

public sealed record FindingView(ItemRefView Item, string? Severity, string? Source);

public sealed record NoteView(
    Guid Id,
    string Kind,
    string Body,
    ItemRefView? From,
    ItemRefView? To,
    DateTimeOffset? ConsumedAt,
    DateTimeOffset CreatedAt,
    SessionView? Session);

public sealed record LinkView(string Type, ItemRefView Item);

public sealed record LinksView(IReadOnlyList<LinkView> Out, IReadOnlyList<LinkView> In);

public sealed record AssumptionView(string Ref, string Text, string Status, ItemRefView? RevisitWhen);

public sealed record ItemPageView(
    DateTimeOffset AsOf,
    long Seq,
    ItemRefView Item,
    IReadOnlyList<ItemRefView> Path,
    string? Intent,
    string? VerifyCmd,
    IReadOnlyList<string> RiskTags,
    short Priority,
    string? Severity,
    string? Source,
    string? Resolution,
    string? ClosedBy,
    DateTimeOffset OpenedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? ClosedAt,
    IReadOnlyList<CriterionView> Criteria,
    ClaimView? Claim,
    IReadOnlyList<ReportView> Reports,
    IReadOnlyList<VerificationView> Verifications,
    IReadOnlyList<ArtifactView> Artifacts,
    IReadOnlyList<ChildView> Children,
    IReadOnlyList<FindingView> Findings,
    IReadOnlyList<NoteView> Notes,
    LinksView Links,
    IReadOnlyList<AssumptionView> Assumptions,
    IReadOnlyList<JournalEventView> Events,
    bool EventsHasMore,
    ImportedView? Imported = null,
    string? Due = null,
    string? NotBefore = null,
    bool Late = false,
    int Slips = 0,
    bool VerifyManual = false);

/// <summary>Set on an item <c>work_import</c> wrote: its source in the repo, and why it was judged still true.</summary>
public sealed record ImportedView(
    string Path,
    string? Anchor,
    string Why,
    string ImportedBy,
    SessionView? Session,
    DateTimeOffset At);

/// <summary>
/// <c>what</c> and <c>call</c> are for agents; the rest is what a person is shown. <c>audience</c>:
/// <c>human</c> or <c>controller</c>. <c>detail</c> is one plain line of at most about 200 chars.
/// </summary>
public sealed record InboxRowView(
    string Key,
    string Kind,
    ItemRefView Item,
    ItemRefView? Run,
    string What,
    DateTimeOffset Since,
    string Call,
    string Audience,
    string? Detail,
    string? Actor,
    string? Tree,
    ItemRefView? Target,
    int Count,
    string? Url);

public sealed record InboxView(DateTimeOffset AsOf, long Seq, IReadOnlyList<InboxRowView> Rows);

public sealed record JournalPageView(
    DateTimeOffset AsOf,
    long Seq,
    long Cursor,
    bool HasMore,
    IReadOnlyList<JournalEventView> Events);

public sealed record BacklogGroupView(ItemRefView? Origin, IReadOnlyList<ItemRefView> Items);

public sealed record BacklogView(DateTimeOffset AsOf, long Seq, ItemRefView? Run, IReadOnlyList<BacklogGroupView> Groups);

/// <summary><c>token</c> (additive): <c>none</c>, <c>live</c>, <c>revoked</c> or <c>expired</c>.</summary>
public sealed record SessionHoldingView(SessionView Session, IReadOnlyList<ClaimView> Claims, string Token = "none");

public sealed record SessionsView(DateTimeOffset AsOf, long Seq, IReadOnlyList<SessionHoldingView> Sessions);

public sealed record ResolveView(DateTimeOffset AsOf, long Seq, IReadOnlyList<ItemRefView> Matches);

public sealed record PromptView(string Key, string Label, int? Count, string Text);

public sealed record PromptsView(DateTimeOffset AsOf, long Seq, ItemRefView Scope, IReadOnlyList<PromptView> Prompts);

/// <summary>One scope's prompts in a <c>scopes=</c> read.</summary>
public sealed record ScopePromptsView(ItemRefView Scope, IReadOnlyList<PromptView> Prompts);

public sealed record ManyPromptsView(DateTimeOffset AsOf, long Seq, IReadOnlyList<ScopePromptsView> Scopes);

/// <summary><c>match</c>: <c>ref</c>, <c>prefix</c> or <c>text</c>; <c>snippet</c> only on a text hit.</summary>
public sealed record SearchHitView(ItemRefView Item, ItemRefView? Run, ItemRefView? Project, string Match, string? Snippet);

public sealed record SearchView(DateTimeOffset AsOf, long Seq, IReadOnlyList<SearchHitView> Hits);

/// <summary>
/// One agenda row (deadlines/6): <c>work_agenda</c>'s line. <c>dueEnd</c> is the instant the due is read as in the
/// workspace's zone; <c>readySince</c> when it last became actionable; <c>holder</c> a live claim's session or agent.
/// </summary>
public sealed record AgendaRowView(
    ItemRefView Item,
    string Status,
    string? Due,
    string? NotBefore,
    DateTimeOffset? DueEnd,
    DateTimeOffset? ReadySince,
    string? Holder,
    IReadOnlyList<ItemRefView> Blockers);

/// <summary><c>until</c> and <c>since</c> are the instants the window was read as, in <c>zone</c>.</summary>
public sealed record AgendaView(
    DateTimeOffset AsOf,
    long Seq,
    string Zone,
    DateTimeOffset Until,
    DateTimeOffset Since,
    ItemRefView? Scope,
    IReadOnlyList<AgendaRowView> Overdue,
    IReadOnlyList<AgendaRowView> Due,
    IReadOnlyList<AgendaRowView> Actionable);

/// <summary>
/// One dated item of <c>/calendar</c>: <c>due</c> and <c>notBefore</c> as written, <c>dueEnd</c> and <c>notBeforeStart</c>
/// the instants they are read as; <c>late</c> is done at or after its due end; <c>closedAt</c> is set once it is done.
/// </summary>
public sealed record CalendarRowView(
    ItemRefView Item,
    string Kind,
    string Status,
    string? Due,
    string? NotBefore,
    DateTimeOffset? DueEnd,
    DateTimeOffset? NotBeforeStart,
    bool Late,
    DateTimeOffset? ClosedAt,
    string? Holder,
    IReadOnlyList<ItemRefView> Blockers);

/// <summary><c>from</c> and <c>to</c> are the instants the range was read as, in <c>zone</c>.</summary>
public sealed record CalendarView(
    DateTimeOffset AsOf,
    long Seq,
    string Zone,
    DateTimeOffset From,
    DateTimeOffset To,
    ItemRefView? Scope,
    IReadOnlyList<CalendarRowView> Items);
