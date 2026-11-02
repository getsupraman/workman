using Workman.Core.Domain;

namespace Workman.Core.Rendering;

public enum PromptScope
{
    Project,
    Run,
    Item,
}

/// <summary>
/// What the catalogue's predicates and templates read about one scope. <see cref="Ref"/> is the
/// callable form: bare while unique in the workspace, else the qualified form that resolves alone.
/// <see cref="Open"/> and <see cref="Blocked"/> count the leaves not closed, and the blocked ones,
/// under a project or a run; <see cref="Rules"/> the active binding rules a review of it reads.
/// </summary>
public sealed record PromptFacts(
    PromptScope Scope,
    string Ref,
    string Kind,
    string Title,
    ItemStatus Status,
    int Unclosed = 0,
    int Backlog = 0,
    string? BacklogRef = null,
    bool Finalizable = false,
    int DoneLeaves = 0,
    int Open = 0,
    int Blocked = 0,
    int Rules = 0);

/// <summary>One button: its label, the count it shows if any, and the whole prompt it copies.</summary>
public sealed record PromptAction(string Key, string Label, int? Count, string Text);

/// <summary>
/// doc 13 "Prompt actions" — the prompts the dashboard copies for a fresh session. Each states a goal
/// in the words of docs/language.md and leaves the tools to the AI and the server instructions. Each
/// starts from the brief of the scope it was copied from (<see cref="From"/>), and any prompt that
/// closes, cancels, defers, merges, moves or archives carries <see cref="ProposeAndWait"/>.
/// The order of <see cref="Entries"/> is the order of the buttons.
/// </summary>
public static class PromptCatalogue
{
    public const string ProposeAndWait =
        "Propose first and wait for the human's yes: list every change you intend to make, and write nothing until the human answers yes.";

    public const string NoWrites = "Make no writes.";

    public const string NoForce = "Never force anything past the done guard.";

    public sealed record Entry(
        string Key,
        string Label,
        PromptScope Scope,
        Func<PromptFacts, bool> Applies,
        Func<PromptFacts, int?> Count,
        Func<PromptFacts, string> Text,
        bool Writes);

    public static readonly IReadOnlyList<Entry> Entries =
    [
        new("continue", "Continue", PromptScope.Project, _ => true, _ => null, Continue, Writes: false),
        new("status", "Where are we", PromptScope.Project, _ => true, _ => null, Status, Writes: false),
        new("plan", "Plan a feature", PromptScope.Project, _ => true, _ => null, Plan, Writes: false),
        new("cleanup", "Clean up", PromptScope.Project, f => f.Open > 0 || f.Unclosed > 0, f => f.Open, Cleanup, Writes: true),
        new("stepback", "Step back", PromptScope.Project, f => f.Open > 0, _ => null, StepBackProject, Writes: true),
        new("rules", "Review rules", PromptScope.Project, f => f.Rules > 0, f => f.Rules, ReviewRulesProject, Writes: true),
        new("triage", "Triage backlog", PromptScope.Project, f => f.Backlog > 0, f => f.Backlog, Triage, Writes: true),
        new("finalize", "Finalize", PromptScope.Project, f => f.Finalizable, _ => null, Finalize, Writes: true),
        new("resume", "Resume", PromptScope.Run, f => !Closed(f.Status), _ => null, Resume, Writes: false),
        new("finalize-run", "Finalize run", PromptScope.Run, f => !Closed(f.Status) && f.DoneLeaves > 0, f => f.Open, FinalizeRun, Writes: true),
        new("stepback", "Step back", PromptScope.Run, f => !Closed(f.Status) && f.Open > 0, _ => null, StepBackRun, Writes: true),
        new("rules", "Review rules", PromptScope.Run, f => !Closed(f.Status) && f.Rules > 0, f => f.Rules, ReviewRulesRun, Writes: true),
        new("blocking", "What's blocking", PromptScope.Run, f => !Closed(f.Status) && f.Blocked > 0, f => f.Blocked, Blocking, Writes: false),
        new("audit", "Did we cheat?", PromptScope.Run, f => f.DoneLeaves > 0, _ => null, Audit, Writes: false),
        new("work", "Work on this", PromptScope.Item, f => Workable(f.Kind) && f.Status is ItemStatus.Open or ItemStatus.Ready, _ => null, Work, Writes: false),
        new("unblock", "Investigate block", PromptScope.Item, f => f.Status == ItemStatus.Blocked, _ => null, Unblock, Writes: false),
        new("stepback", "Still needed?", PromptScope.Item, f => Workable(f.Kind) && f.Status is ItemStatus.Open or ItemStatus.Ready or ItemStatus.Blocked, _ => null, StepBackItem, Writes: true),
    ];

    /// <summary>The actions that apply to <paramref name="facts"/>, in catalogue order.</summary>
    public static IReadOnlyList<PromptAction> For(PromptFacts facts)
        => [.. Entries
            .Where(e => e.Scope == facts.Scope && e.Applies(facts))
            .Select(e => new PromptAction(e.Key, e.Label, e.Count(facts), e.Text(facts)))];

    public static bool Closed(ItemStatus status)
        => status is ItemStatus.Done or ItemStatus.Cancelled or ItemStatus.Superseded;

    /// <summary>The sentence every prompt starts its work with: the brief of its own scope.</summary>
    public static string From(PromptFacts f, bool worker = false)
        => $"Start from the {(worker ? "worker brief" : "brief")} for `{f.Ref}`";

    private static bool Workable(string kind) => kind is "task" or "followup" or "subtask";

    private static string Lines(params string[] lines) => string.Join("\n", lines);

    private static string Named(PromptFacts f) => $"Workman {f.Kind} `{f.Ref}` ({f.Title})";

    private static string Continue(PromptFacts f)
        => Lines(
            $"Pick up where we left off in {Named(f)}.",
            $"{From(f)} and do what its next step says: resume the run it names, or, when no run is open, tell me which work you would start and why, and wait.",
            "Work the loop: take the next ready task, do it, verify it yourself, and keep going until the run is through or something needs me. Then stop and say what.");

    private static string Status(PromptFacts f)
        => Lines(
            $"Tell me where {Named(f)} stands. {NoWrites}",
            $"{From(f)}, then look at the shape of its runs and at its loose ends.",
            "Answer in three short parts:",
            "- what landed: what is done, with the evidence recorded for it;",
            "- what is open: each run, where it stands and what comes next in it;",
            "- what needs me: blocked items, open questions, decisions and assumptions waiting on a person.",
            "Name every item by its short ref, as a link.");

    private static string Plan(PromptFacts f)
        => Lines(
            $"Plan a feature in {Named(f)}: <describe the feature here>.",
            $"{From(f)}. Check the code before you plan: the feature may already exist, whole or in part, and work already open in the project may overlap it.",
            "Write the plan as a new run: its goal in one paragraph; tasks, in phases if it is one connected plan, each with done-criteria that state outcomes and a verification command that could actually fail; the binding rules the work must keep; the assumptions you are making; and every open decision as a question.",
            "Then show me the run and stop. Build nothing until I approve the plan.");

    private static string Cleanup(PromptFacts f)
        => Lines(
            $"Clean up and reorganize {Named(f)}: {f.Open} open item(s) and {f.Unclosed} loose end(s).",
            $"{From(f)}, then read every open item in the project and every loose end: findings, follow-ups, open assumptions, notes addressed to an item, expired claims, work left in progress.",
            "Check each against the CURRENT code: read the files it names, look for the commit that did it. Then sort it:",
            "- done in the code: close it with evidence - run its verification yourself first;",
            "- stale or no longer wanted: cancel it, with the reason;",
            "- wanted, but not now: defer it to the backlog - a deferral is a move, never a drop;",
            "- two items for one piece of work: merge them - keep one, and mark the other a duplicate of it;",
            "- out of date: update its title, intent or criteria to what is true now;",
            "- an assumption: confirm or disprove it, with the evidence;",
            "- an expired claim or a stale in-progress item: find out whether the work finished, then close, defer or restart it.",
            "Then restructure what stays into clear runs - one goal each, phases where the work is connected - and order it so what others wait on comes first.",
            NoForce,
            ProposeAndWait);

    private static string StepBackProject(PromptFacts f)
        => StepBack(
            $"Step back on {Named(f)}: does the open work still make sense?",
            $"{From(f)}. Read the project's goal, then every open run and every backlog item.",
            "the project's goal");

    private static string ReviewRulesProject(PromptFacts f)
        => ReviewRules(f, "those on the project");

    private static string ReviewRulesRun(PromptFacts f)
        => ReviewRules(f, "those on the run and its project");

    private static string ReviewRules(PromptFacts f, string where)
        => Lines(
            $"Review the binding rules of {Named(f)} and realign them with the code and the standing decisions: {f.Rules} active rule(s).",
            $"{From(f)}, then read every binding rule active in scope: the workspace-wide ones, {where}, and those on anything inside it. Read the decision notes and the answered questions too: they hold what the human has decided since.",
            "Check each rule:",
            "- is it still true of the CURRENT code? The files, commands, hosts and names it cites may have moved or been renamed - look them up;",
            "- does it contradict a newer decision or a standing approval the human gave?",
            "- is it a duplicate of another rule, too vague to act on, or a red line without its route?",
            "- is its kind right (red line, command, stack fact, domain rule, generated code), and does it bind the right scope?",
            "For each, answer keep, reword, retire or rescope, with the evidence: a file and line, a commit, or the note that decided it. A rule is replaced, never edited: to reword or rescope one, retire it and add the new one.",
            "Then propose the rules that are missing: what the code or the recent decisions show should bind the work and nothing says yet.",
            ProposeAndWait,
            "Never retire a red line without the human's explicit yes for that rule.");

    private static string Triage(PromptFacts f)
    {
        var backlog = f.BacklogRef is { } run ? $" `{run}`" : string.Empty;
        return Lines(
            $"Triage the backlog of {Named(f)}: {f.Backlog} open item(s) - the backlog run{backlog} and every open follow-up in the project.",
            $"{From(f)}, then read each of them in full.",
            "Check each against the CURRENT code before you judge it: read the files it names. What is already done or no longer applies is a drop, with the evidence (a file and line, or a commit sha).",
            "Sort each into do now, later, drop, or needs a decision - and say whose decision.",
            "Propose a queue of the do-now items in dependency order: what others wait on first.",
            ProposeAndWait,
            "Create no runs and cancel nothing before that yes.");
    }

    private static string Finalize(PromptFacts f)
        => Lines(
            $"Finalize {Named(f)}: no run in it has open work left.",
            $"{From(f)} and confirm that nothing open is left outside the backlog.",
            "- carry any leftover to the backlog: defer it, with the reason;",
            "- close each run still open, with evidence: run its verification yourself first;",
            "- then archive the project.",
            ProposeAndWait,
            $"Ask again before each close and before archiving. {NoForce}");

    private static string Resume(PromptFacts f)
        => Lines(
            $"Resume {Named(f)} where we left off.",
            $"{From(f)} and do what its next step says.",
            "Work the loop: take the next ready task, do it, verify it yourself, and keep going until the run is through or something needs me. Then stop and say what.");

    private static string FinalizeRun(PromptFacts f)
        => Lines(
            $"Finalize {Named(f)}: {f.Open} open item(s) left.",
            $"{From(f)}, then read everything still open in the run and its loose ends.",
            "1. Clean up what is left: what is done in the code gets closed with evidence, what is stale gets cancelled with the reason, what can wait gets deferred to the backlog, and the rest is reordered so what others wait on comes first.",
            "2. Explain what is missing to close the run: for each item still open, what the done guard still wants (an unchecked criterion, a verification you have not run, an open child, a blocker finding), and for each phase whether its gate has run.",
            "3. Offer to start the remaining work, in that order.",
            ProposeAndWait,
            $"Then do only what I approved. {NoForce}");

    private static string StepBackRun(PromptFacts f)
        => StepBack(
            $"Step back on {Named(f)}: does this run still make sense?",
            $"{From(f)}. Read the run's goal, then every open item in it.",
            "the run's goal");

    private static string Blocking(PromptFacts f)
        => Lines(
            $"What is blocking {Named(f)}? {NoWrites}",
            $"{From(f)}, then find every blocked item in the run and everything that holds work up: open questions, blocker findings, dependencies not yet done, expired claims.",
            "For each: what it waits on, who or what could unblock it - a person's decision, another run, an outside system - and exactly what would unblock it. Do none of it.",
            "End with the one thing that would free the most work.");

    private static string Audit(PromptFacts f)
        => Lines(
            $"Review {Named(f)}: did we cheat? {NoWrites}",
            $"{From(f)}, then run Workman's audit on the run and read the evidence of every item marked done.",
            "Compare what the agents reported with what was actually verified, and look for:",
            "- done with no verification an orchestrator ran;",
            "- a verification whose command is not the item's verification command, or one that could never fail;",
            "- a report that claims more than the evidence shows;",
            "- criteria ticked without evidence, or reworded to fit what was built;",
            "- a transition forced past the done guard;",
            "- work deferred or cancelled to make the run look finished.",
            "Then check a few done items against the code itself. List every gap with its ref and what would settle it.");

    private static string Work(PromptFacts f)
        => Lines(
            $"Work on {Named(f)}.",
            $"{From(f, worker: true)}: it holds the done-criteria, the verification command and the binding rules.",
            "Do the work, run the verification command yourself, then hand it back with a report: the files you changed, the verification's result line, what you assumed, and what you did not do.",
            "Do not mark it done yourself unless you hold the orchestrator role: the done guard wants a verification someone other than the worker ran.");

    private static string Unblock(PromptFacts f)
        => Lines(
            $"Find out why {Named(f)} is blocked. {NoWrites}",
            $"{From(f)}, then read the item with what it depends on and its loose ends.",
            "Report what it waits on, who or what could unblock it, and exactly what would. Do none of it.");

    private static string StepBackItem(PromptFacts f)
        => Lines(
            $"Is {Named(f)} still needed?",
            $"{From(f)} and read the item in full.",
            "The code may have moved on since it was written. Check it against the current code and the goal of its run: is it done already, done in part, made moot by other work, or still exactly right? Do its criteria still say what done means?",
            "Answer keep, rewrite, merge, move, defer or cancel, with the evidence: a file and line, or a commit.",
            ProposeAndWait);

    private static string StepBack(string goal, string from, string against)
        => Lines(
            goal,
            from,
            $"Much of it was written before the code moved on. Question each item against the current code and {against}:",
            "- is it still needed, or did other work make it moot?",
            "- is it still the right shape: the right size, in the right run, with criteria that still say what done means?",
            $"- does it still serve {against}, or has that changed?",
            "- what is missing: work the goal needs that nothing open covers?",
            "Be blunt: an item that no longer earns its place should go. For each, answer keep, rewrite, merge, move, defer or cancel, with the evidence: a file and line, or a commit.",
            ProposeAndWait);
}
