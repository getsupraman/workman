# The Workman language

How to talk to an AI that keeps its work in Workman. You don't need to know the tools. The server
gives every session [its instructions](05-server-instructions.md) and the AI picks the calls itself.
You state the goal using the words on this page, and the AI knows what each one means in the store.

Each term below says what it is, when you'd use it, and something you might actually say. Then come
the phrases the AI acts on, and a few short conversations. [concepts.md](concepts.md) explains the
model behind the words. [07-guards.md](07-guards.md) has the exact rules for closing work.

## Where work lives

**Project.** A repository, or any long-lived body of work. Everything else sits under one.
*Use it* to say which body of work you mean, or to start a new one. *Say:* "Start a Workman project
for this repo." "Which project is this in?"

**Feature.** An optional grouping between a project and its runs, for work that spans several runs.
*Say:* "Group the checkout runs under a feature."

**Run.** One undertaking inside a project, with a goal: a feature, a fix, a migration, a release.
Several runs can be open at once. *Use it* for anything bigger than a single task. *Say:* "Make
this a run." "What's the goal of this run?" "Close the run."

**Phase.** A stage of a run that holds tasks, for work that goes in steps. *Say:* "Split the run into
three phases: schema, API, dashboard."

**Gate.** The check at the end of a phase: does the phase as a whole meet its goal, beyond each task
passing on its own? The AI runs it from the phase's gate brief, and anything it finds becomes a
finding. *Say:* "Gate phase 2 before you start phase 3."

**Task.** One piece of work, with done-criteria and a verification command. Workman refuses to create
a task without both. A task can hold **subtasks**, and tasks can nest inside tasks. *Say:* "Add a task
for the migration." "Break 2.3 into subtasks."

**Backlog.** The project's run for work that belongs to no run yet. It's where deferred work goes.
*Say:* "Put it on the backlog." "What's on the backlog?"

**Ref.** The name of an item. Inside a run it is a number (`2.3`, `F-07`); from elsewhere it is
qualified (`checkout-v2/2.3`). Items under a project or its backlog get a short readable name
(`saved-carts`). Every ref has a link, `/r/<ref>`, that opens it in the dashboard. The AI never
invents a ref. It reads one from the store and shows it to you as a link. *Say:* "What's 2.3?" "Link
me the item."

## Things that are not tasks

**Finding.** Something found wrong, by a review, a gate, an audit or the AI itself. It has a
severity: a **blocker** stops its target from being closed until it is settled; a **minor** one
doesn't. *Use it* when something is wrong and must not get lost. *Say:* "Log that as a finding
against 2.4." "Is that a blocker?" A finding belongs to the item it sits under: when an audit becomes
units, each finding moves under the unit that will fix it, and that unit settles it when it closes —
done with its commit, or deferred with a target and the reason. A run does not close while a finding
under it is open.

**Follow-up.** Something to do later, written down now so it is not lost in chat. *Say:* "Make a
follow-up for the flaky test." "Note it as a follow-up and carry on."

**Question.** A decision that someone has to make, held as an item. Work that waits on it is blocked.
It closes only with its answer, and the dashboard then shows it as *Answered*. *Use it* when the AI
should not decide for you. *Say:* "Make that a question for me." "The answer to Q1 is: keep the old
API until March."

**Spike** and **incident.** A spike is time-boxed exploration. An incident is something that went
wrong in operation. Both are items with a state. *Say:* "Open a spike: can we stream the export?"

## Done, and how it is proven

**Criterion** (done-criteria). A statement of what must be true when the item is finished, written as
an outcome ("the export streams a 1 GB file under 200 MB of memory"), not as an activity ("work on the
export"). Each one is checked off with its evidence. *Say:* "Add a criterion: the old endpoint
answers 410." "Which criteria are still open on 2.3?"

**Verification command.** The command an item is judged by. It has to be able to fail if the work is
wrong. A test filter that only matches tests already passing doesn't count. *Say:* "What's the verify
command for 2.3?" "That command can't fail. Pick one that would."

**Verification.** The record that a command was run: the command, its actual result line
(`412 passed`, not "it worked"), and who ran it. **Evidence** is what backs a criterion or a close: a
verification, a file and line, a commit. *Say:* "Show me the evidence for 2.3." "Run it yourself and
record the result."

**Report.** A worker's own account of what it did: files, the verification it ran, assumptions,
risks and what it didn't do. Workman records it as a claim about the work, never as proof. The
dashboard shows *What the agent reported* beside *What was verified*. *Say:* "Hand it back with a
report."

**Red-check.** Proof that a test can fail. Revert the fix, watch the test go red, then put the fix
back. It is recorded with the verification. *Say:* "Red-check that test."

**Done guard.** The rule that decides when an item may close. Closing is refused unless every
criterion is checked, an orchestrator (not the worker who did the work) ran a passing verification,
no child is still open, and no blocker finding targets the item. The refusal lists exactly what is
missing. *Say:* "What does the done guard still want for the run?" "Why won't 2.3 close?"

**Force.** The override that closes past the done guard. It needs a reason, and the audit lists
every forced close for good. Ask for it by name if you really mean it. A prompt from the dashboard
never asks for it. *Say:* "Don't force anything."

## What binds the work, and what is known

**Binding rule.** A rule that binds a project or a run. Every brief in its scope prints it with its
ref, so it can be cited, questioned or retired. The kinds:

- **red line**: something that must never happen, always with the route to take instead ("never
  edit generated files; regenerate them with `make gen`"). Workman refuses a red line without a
  route.
- **command**: the command to use for something ("tests run with `dotnet test --filter Unit`").
- **stack fact**: a fact about the stack the work must respect ("Postgres 17, no extensions").
- **domain rule**: a rule of the problem itself ("an invoice number is never reused").
- **generated code**: code that is generated, so it is regenerated rather than edited by hand.

*Use it* when you catch yourself saying the same thing twice. *Say:* "Make that a binding rule."
"Make it a red line: never push to main; open a PR." "Retire rule C3, it's obsolete." "Realign the
rules with the code."

A rule is never edited in place. To reword it or change what it binds, the AI retires the old one
and adds the new one, so the record shows what bound the work and when that changed.

**Assumption.** A decision taken on incomplete knowledge. It records how sure the AI is, what
reversing it would cost and when to look at it again. Past that point it shows up in every
attention block until someone confirms or disproves it, or marks it obsolete. *Say:* "Record that as an
assumption." "What are we assuming?" "Confirm A2, here's the proof."

**Note.** Something that isn't a task but must be carried forward. The kinds are a **decision** and
its reason, a **correction**, an **incident**, something about the **environment**, a fact the next
agent should **not re-derive**, and a **carry-forward**. A note can be **addressed to an item**. It
then appears in that item's brief for whoever picks it up. *Say:* "Write a decision note: we drop
IE support." "Leave a note on 3.1 for whoever does it."

## Who is working, and how

**Brief.** The recovery payload a session starts from: the goal, where things stand, the next step,
the binding rules, open assumptions and notes addressed to you. It is built from the record, so it
can't go stale, and it is what the AI reads after its context is compacted. A **worker brief** is the
one a worker gets for one item: its criteria, its verification command and its rules. A **gate
brief** is the one for gating a phase. *Say:* "What's the brief?" "Read the brief first."

**The loop.** What every session does: read the brief, take the next ready item, start it, do the
work, verify it, close it, take the next. *Say:* "Work the loop." "Keep going until something needs
me."

**Next.** The items whose dependencies are all done, in order. *Say:* "What's next?"

**Claim.** Starting an item takes a claim on it with a **lease**, so two sessions can't silently work
the same thing. When the lease lapses the item goes back to ready and the attention block says so.
*Say:* "Who holds 2.4?" "The claim on 2.4 expired. Find out if the work finished."

**Attention.** The block at the end of every result about work you are *not* doing right now:
expired claims, assumptions past their revisit point, notes for the item you're about to start. *Say:*
"Anything in attention?"

**Roles.** A **worker** does one item and hands it back with a report. It can't close, create or
re-plan. An **orchestrator** plans, judges, closes and commits. A **controller** runs a queue of
units across nested sessions. The role comes from the token, and a tool the role lacks is hidden, not
refused. *Say:* "You're the worker on this one. Don't close it." "Orchestrate this run."

**Session.** Each connected Claude session is a record with a name, so the dashboard and the journal
show who did what. *Say:* "Name this session after the branch."

## Moving work

**Start**, **block**, **review.** An item goes from open to ready (nothing blocks it), to in
progress (claimed), optionally to review, and then to done. Blocked needs a reason. *Say:* "Mark 2.4
blocked: it waits on the vendor's API key."

**Close.** Done, through the done guard, with evidence. *Say:* "Close 2.3." "Close the run." If the
guard refuses, the AI tells you what is missing instead of forcing it.

**Defer = move.** Workman has no "deferred" pile where work goes to die. Deferring moves the item to
a real place, usually the backlog, as a new open item, and marks the original *Moved*. It always
takes a reason. *Say:* "Defer it to the backlog." "Not now. Move it to the next run."

**Cancel.** Ends an item that is no longer wanted, with a reason. Nothing is deleted. Workman has no
delete, and the record stays. *Say:* "Cancel 4.2, the feature was dropped."

**Supersede** and **duplicate** (merge). When two items are one piece of work, keep one and link the
other to it. Superseding closes the old one as *superseded*. *Say:* "Merge 3.4 into 3.2." "2.1b
supersedes 2.1."

**Move** and **reorder.** Put an item under another parent, or before or after a sibling. Nothing
is renumbered. *Say:* "Move the auth tasks into their own run." "Do 3.2 before 3.1."

**Blocks** (dependencies). One item waits on another. That decides what is ready. *Say:* "3.1 blocks
3.4." "What does 3.4 wait on?"

**Archive.** Hides a finished project from the lists. It isn't deleted, and you can unarchive it.
*Say:* "Archive the project."

## Looking

**Status** ("where are we"). What landed, what is open, what needs you. **Tree**: the shape of a run
or project with its counts. **Loose ends** (threads): everything unclosed that isn't a task, like
assumptions, findings, follow-ups, addressed notes and stale work. **Audit**: the mechanical check of
a run, covering done without verification, forced closes, expired claims and verify commands that
have never been seen failing. **History**: what happened, event by event. **Search**: everything
written, across items, rules, assumptions and notes.

*Say:* "Where are we?" "Show me the tree of the run." "What loose ends are there?" "Audit the run."
"What happened while I was away?" "Have we written anything about rate limits?"

## Phrases, and what the AI does

| You say | The AI |
|---|---|
| "What's the brief?" / "Where were we?" | reads the brief of the scope and tells you the goal, where things stand and the next step |
| "Continue." / "Work the loop." | takes the next ready item, does it, verifies it itself, closes it, and repeats until something needs you |
| "Plan this feature." | checks the code first, then writes a run: a goal, tasks with criteria and a verify command, rules, assumptions, questions. Then it stops and waits for your approval |
| "Make that a binding rule." / "Make it a red line." | records a rule on the project or run, with the route for a red line, so every later brief prints it |
| "Record that as an assumption." | records it with how sure it is, the cost of reversing it and when to look again |
| "Make that a question for me." | creates a question item and marks the work that waits on it blocked, so it can't decide for you |
| "Log a finding." | records a finding against the item, with its severity |
| "Note it as a follow-up." | creates a follow-up, usually on the backlog, with a short readable ref |
| "Defer it to the backlog." | moves the item to the backlog with your reason. Nothing is dropped |
| "Cancel it." | cancels it with your reason. The record stays |
| "Merge these." | keeps one and marks the other as its duplicate, or superseded by it |
| "Close it." / "Close the run." | runs the verification itself, records the evidence and closes. If the done guard refuses, it tells you what is missing |
| "Clean up the project." | reads every open item, checks each against the code, then *proposes* closes, cancels, merges, moves and updates, and waits for your yes |
| "Step back." | questions each open item against the current code and the goal, and proposes keep, rewrite, merge, move, defer or cancel |
| "Triage the backlog." | sorts the backlog into do now, later, drop and needs a decision, and proposes an ordered queue |
| "Realign the rules." / "Review the rules." | reads every active binding rule in scope, checks each against the current code and your later decisions, and proposes keep, reword, retire or rescope, plus the rules that are missing. It never retires a red line without your yes for that rule |
| "Did we cheat?" | compares what was reported with what was verified and lists every gap, without changing anything |
| "What's blocking?" | lists every blocked item, what it waits on, and who or what could free it |
| "Where are we?" | reports what landed, what is open and what needs you |

Anything that closes, cancels, defers, merges, moves, archives or retires in bulk is proposed first. The AI
lists every change and writes nothing until you say yes. A single, explicit instruction ("cancel
4.2") is that yes.

## Short conversations

**Picking up the next morning.**

> **You:** Where were we on checkout?
> **AI:** *(reads the brief for the project)* Run `checkout-v2` is in phase 2. 2.1 and 2.2 are
> done and verified. 2.3 is next and ready. One assumption is past its revisit date: A2, "the
> payment provider keeps the v1 webhook until Q1".
> **You:** Check A2, then carry on.
> **AI:** A2 is confirmed by the provider's changelog, so I recorded the evidence. Starting 2.3.

**Turning a remark into a rule.**

> **You:** Don't touch the generated client again, regenerate it.
> **AI:** Shall I make that a red line on the project? "Never edit `src/gen/`; run `make gen`."
> **You:** Yes, make it a red line.
> **AI:** Done. It is rule C4, and every brief in the project prints it.

**Deferring instead of dropping.**

> **You:** The CSV export can wait. Defer it to the backlog.
> **AI:** Moved 3.4 to the backlog as `csv-export`, with the reason "after the API ships". 3.4 now
> reads *Moved*.

**Closing a run that isn't ready.**

> **You:** Close the run.
> **AI:** The done guard refuses: criterion 2 of 2.5 is unchecked, and finding F-03 (blocker) is open
> against phase 2. I can fix F-03 and verify 2.5, or defer F-03 to the backlog with a reason. Which
> do you want?

**A question the AI must not answer itself.**

> **You:** Should deleted users keep their invoices? I don't know yet.
> **AI:** I made that a question, Q1, and marked 4.2 blocked on it. It's in your inbox on the
> dashboard.
> *(later)* **You:** The answer to Q1: keep them, anonymised.
> **AI:** Q1 is answered and closed, and the answer is recorded as a decision note. I've unblocked 4.2.

**Stepping back.**

> **You:** Step back on the backlog. Half of it is from before the rewrite.
> **AI:** Of 14 items: 5 are done in the code (each with its commit), 3 no longer apply since the
> rewrite, 2 are duplicates of open tasks, and 4 still matter. Here's the table of what I'd close,
> cancel, merge and keep. I'll write nothing until you say yes.
> **You:** Yes, except keep `bulk-import`.

**Realigning the rules.**

> **You:** Realign the rules with the code. We moved to pnpm last month.
> **AI:** *(reads every active rule in the project and the decision notes)* Of 9 rules: C2 says "run
> `npm test`", but the repo uses pnpm since commit `4f1c2e9`, so I'd retire it and add "run
> `pnpm test`". C5 cites `src/legacy/`, which is gone, so retire it. C7 duplicates C3, so retire C7.
> C1 is a red line ("never edit `src/gen/`") and still true, so keep it. The decision note from
> 12 October approves pushing straight to the release branch, which contradicts red line C4. Retire
> C4? I need your yes for that one by name. One rule is missing: the new `make gen` step. I've
> written nothing yet.
> **You:** Yes to all, including retiring C4.

**Did we cheat?**

> **You:** Review the run. Did we cheat?
> **AI:** Two gaps. 1.4 was closed on a worker's verification only, and 2.2's verify command
> filters on a test that passed before the change, so it couldn't fail. I've changed nothing.

## Prompts on the dashboard

The dashboard has buttons that copy a ready prompt in these words for a fresh session: Continue,
Where are we, Plan a feature, Clean up, Step back, Review rules, Triage backlog and Finalize on a
project; Resume, Finalize run, Step back, Review rules, What's blocking and Did we cheat? on a run; Work on this, Investigate block
and Still needed? on an item. Each prompt starts from the brief of the scope it was copied from, and
every prompt that changes the shape of the work proposes first and waits for your yes. The full table
is in [13-dashboard.md](13-dashboard.md#the-prompt-catalogue).

## Words Workman doesn't use

- **Delete.** Nothing is deleted. Cancel, supersede or archive instead, and the record stays.
- **A "deferred" status.** Deferring is a move to a real place, never a parking state.
- **Done without evidence.** "It's done, I checked" is a report. Done needs a verification someone
  other than the worker ran.
- **PLAN.md or LEDGER.md.** The AI writes the plan and the ledger into Workman, not into files. To
  bring an existing plan file in, use `/workman:import`.
