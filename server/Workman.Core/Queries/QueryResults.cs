using Workman.Core.Domain;

namespace Workman.Core.Queries;

/// <summary>A row of the ready frontier: work that is actionable right now, in the order to take it.</summary>
public sealed record ReadyItem(
    Guid Id,
    string Ref,
    string Kind,
    string Title,
    string? VerifyCmd,
    string[] RiskTags,
    short Priority,
    decimal Seq);

/// <summary>
/// An edge pointing at work that will never arrive: a blocker that was abandoned, an
/// uncommissioned stub, or an ordering inversion (plan-audit check #7).
/// </summary>
public sealed record UnclosedDependency(
    string Waiting,
    string On,
    ItemStatus Status,
    bool OrderedCorrectly,
    Guid WaitingId,
    Guid? WaitingScopeId,
    Guid OnId,
    Guid? OnScopeId);

/// <summary>
/// The four unclosed buckets plus <see cref="DoneCount"/>. <c>cancelled</c> and <c>superseded</c>
/// are in none of them — they are closed and were not done, so they belong in neither half of an
/// "X of Y" — but <see cref="ReviewCount"/> is work in flight and belongs in the denominator.
/// Only <see cref="WorkmanQueries.Counted"/> descendants are in any bucket.
/// </summary>
public sealed record ItemRollup(
    Guid Id,
    long OpenCount,
    long DoingCount,
    long BlockedCount,
    long ReviewCount,
    long DoneCount);

/// <summary>An item in document order, with the rollup of the counted items beneath it (<see cref="WorkmanQueries.Counted"/>).</summary>
public sealed record SubtreeNode(
    Guid Id,
    Guid? ParentId,
    string Ref,
    string Kind,
    string Title,
    ItemStatus Status,
    int Depth,
    decimal Seq,
    long OpenCount,
    long DoingCount,
    long BlockedCount,
    long ReviewCount,
    long DoneCount);

/// <summary>Everything unclosed that is not a task — the work_threads backbone.</summary>
public sealed record OpenThread(string Type, string Ref, string Text, Guid? Scope);

/// <summary>
/// <c>Source</c> is item, assumption, constraint or note. <c>Tag</c> is the assumption's state, the
/// rule's kind or the note's kind; <c>ScopeId</c>/<c>ScopeRef</c> the item the row belongs to.
/// </summary>
public sealed record SearchHit(
    string Source,
    Guid Id,
    string Ref,
    string Title,
    string Snippet,
    float Rank,
    string? Tag,
    Guid? ScopeId,
    string? ScopeRef);
