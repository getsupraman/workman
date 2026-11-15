# Concepts

What Workman stores, and the loop every session follows. Each section links to the design document
with the full rules.

## Projects, runs and items

Everything is an **item** in one tree:

- A **project** is a repository, or any long-lived body of work. It has a **backlog** for work that
  belongs to no run yet.
- A **run** is one undertaking inside a project, with a **goal**: a feature, a migration, a release.
  Several runs can be open at once, each driven by its own orchestrator.
- A run holds **phases**, and phases hold **tasks** (and sub-tasks). A phase can end with a **gate**:
  a check that the phase as a whole meets its goal.
- **Findings** (something found wrong, possibly a *blocker*) and **follow-ups** (something to do
  later) are items too, so they have a state and can't be lost in prose.

Each item has a **ref**. Inside a run refs are short (`1.2`, `F-07`); from anywhere else they are
qualified (`checkout-v2/1.2`). Items under a project or its backlog get a short readable name
(`saved-carts`). Every ref has a link, `/r/<ref>`, that opens the item in the dashboard; `work_brief`
and `work_get` print it.

An item moves through `open` → `ready` (nothing blocks it) → `in_progress` → `done`, or ends
`cancelled` or `superseded`. Dependencies between items (`blocks`, `derived_from`, …) decide what is
ready. → [02-data-model.md](02-data-model.md)

## Criteria and verification

A task carries **done-criteria** (what must be true when it's finished) and a **verify command** (a
command that could actually fail if it isn't).

- A **report** (`work_report`) is a worker's account of what it did. It is recorded as a **claim**
  about the work, never as proof.
- A **verification** (`work_verify`) records a command, its result line and **who ran it**.

`work_status(ref, "done")` is refused unless every criterion is checked, an orchestrator (not the
worker who did the work) ran a passing verification, no child is still open and no blocker finding
targets the item. The refusal lists every missing condition with the call that fixes it. A `force`
override exists and is never hidden: `work_audit` lists every forced close.

Work is never dropped silently. **Deferring is a move**: `work_status(ref, "deferred", target: …)`
creates a real open item somewhere else, usually the project's backlog. → [07-guards.md](07-guards.md)

## Claims

Starting a task (`work_status(ref, "in_progress")`) takes a **claim** with a **lease**, held by the
session that started it. Two sessions can't silently work the same task. When the lease lapses, the
task goes back to `ready` and the next session's `attention` block says so.

## Rules, assumptions and notes

- **Rules** (`work_constrain`) bind a project or a run: red lines, commands to use, facts about the
  stack. `work_brief` prints the ones that bind the current work, each with its ref so it can be
  cited, questioned or retired.
- **Assumptions** (`work_assume`) are decisions taken on incomplete knowledge, with how sure they are
  and when to revisit them. One past its revisit point shows up in `attention`.
- **Notes** (`work_note`) carry what isn't a task: a decision and its reason, a correction, an
  incident, something about the environment. A note can be **addressed to an item**; it then appears
  in that item's brief for whoever picks it up.

## Roles

The token or the sign-in decides the role, and the role decides which tools a session sees.
**Workers** report and verify. **Orchestrators** plan, judge, close and commit. A **controller**
additionally runs several orchestrated units through nested agents. A worker cannot close, create or
re-plan, because it is never offered the tools. → [01-mcp-surface.md](01-mcp-surface.md)

## The loop

Every session gets the same loop in the server's MCP instructions:

```
work_brief → work_next → work_status(in_progress) → do the work
           → work_verify → work_status(done) → work_next
```

- **`work_brief`** is the recovery payload: the goal, where things stand, the literal next call, the
  rules that bind you, open assumptions and notes addressed to you. It is generated from the record,
  so it can't go stale, and it is what a session calls after compaction.
  → [06-rendering.md](06-rendering.md#4-work_brief--the-recovery-payload)
- **`work_next`** names the next ready item.
- Every result ends with an **`attention`** block: expired claims, assumptions past their revisit
  point, notes for the item you're about to brief.

## Dates

Any item can carry **`due`** (must be done by) and **`not_before`** (not actionable before), set with
`work_create` or `work_update`. Each is a day, `2026-12-01`, meaning that whole day in the workspace's
time zone (UTC unless an admin sets another), or an ISO date-time with an offset. `work_next` leaves
out an item whose `not_before` has not come and ranks overdue, then due-today items first; an item
with no dates behaves as before. Every change of a date is journalled, so `work_get` shows how often a
due slipped and whether an item closed late. Workman never pushes: agents ask (`work_agenda`,
`work_next`, `attention`), people look (the dashboard's **Agenda** page, the calendar feed).
→ [02-data-model.md](02-data-model.md#dates--due-and-not_before)

**Start on a date.** Give the task `not_before` and `due` both on that day:
`work_update(ref:"site-refresh", not_before:"2026-12-01", due:"2026-12-01")`. It stays off `work_next`
until the 1st, then ranks first that day, and is overdue once the day has ended.

**Wait on a client's email.** Waiting on something outside is a **`wait`** item that blocks the real
task; its `due` is the day to chase:

```
work_create(items:[{kind:"wait", parent:"1", ref:"W1",
                    title:"Signed scope from the client", due:"2026-11-24"}])
work_link(from:"W1", to:"1.3", type:"blocks")
```

A wait is never offered by `work_next`; it shows on the agenda by its chase date, and in `attention`
once that has passed. When the email arrives, anyone closes it with what arrived as the evidence, not
a verification: an orchestrator with `work_status(ref:"W1", to:"done", reason:"scope signed, v2
attached")`, a worker with `work_report(ref:"W1", status:"done", verify:"scope signed, v2 attached")`.
Closing it releases 1.3.

**A legal deadline with an earlier warning.** Put the hard date on the filing and a second item, due
earlier, that blocks it:

```
work_create(items:[{kind:"task", parent:"2", ref:"2.1", title:"Draft and check the reply",
                    due:"2027-01-08", …},
                   {kind:"task", parent:"2", ref:"2.2", title:"File the reply",
                    due:"2027-01-15", …}])
work_link(from:"2.1", to:"2.2", type:"blocks")
```

The draft turns overdue a week before the filing does, and if 2.1 is still open as the 15th nears,
`attention` warns that 2.2 is due soon and still blocked by it.

**A run's launch date.** Put `due` on the run itself, and date its phases inside it. `work_audit` flags
`due-after-parent`: an open item due after its own parent is due, both dated; the fix it names is the
child's date or the parent's.

**A scheduled orchestrator check.** An orchestrator woken twice a day (07:30 and 16:30, by a loop or a
cron) starts each check with

```
work_agenda(until:"2026-11-16", since:"2026-11-15T16:30:00+01:00")
```

and gets three lists: **OVERDUE**, **DUE BY** the end of `until`, and **ACTIONABLE SINCE** the last check
(its `not_before` came, or its last blocker closed). That replaces an external todo list. The same data
is `GET /api/v1/agenda`. → [01-mcp-surface.md](01-mcp-surface.md), [10-read-api.md](10-read-api.md)

**The calendar feed.** On the dashboard's Agenda page, below the agenda, create a calendar feed and subscribe to its URL
(`https://workman.example/api/calendar/<token>.ics`) in a calendar app: every open item with a due, in
the projects you can see, as an all-day or a timed event. The URL is shown once and is the secret;
replace or revoke it there. → [10-read-api.md](10-read-api.md)

## The dashboard

The same server serves a dashboard for people: what needs you, who is working on what, every run's
progress, each item's evidence (what was reported next to what was verified), and a search and command
palette that opens any ref. → [13-dashboard.md](13-dashboard.md)
