# Decomposition — phases, tasks, and writing them into the store

Sizing is the highest-leverage decision the orchestrator makes: too big comes back half-done, too small burns agents on bookkeeping and fragments the design.

## The right-sized task

A task is right-sized when **all** of these hold:

- One agent can carry it **end-to-end** in a single context: understand → change → verify.
- It leaves the repo in a **coherent state** — builds, and doesn't break existing tests.
- It has a **single verification command** whose green/red actually means something for this task.
- Its intent fits in **two sentences**, and its done-criteria in **≤6 checkboxes**.
- It touches roughly **≤10 files**, or more only when the change is genuinely mechanical and repetitive.

### Split when
- The task crosses a contract that doesn't exist yet (define the contract in task N, consume it in N+1).
- Two halves have genuinely different verification (backend logic vs. UI wiring).
- One half is high-risk/high-judgment and the other is mechanical — don't make one agent context-switch between modes.
- The file count is large *and* the changes are non-uniform.

### Do NOT split when
- The split produces an intermediate state that can't compile or can't be verified — the next worker would start by debugging its predecessor.
- The two parts must be designed together to be coherent (e.g. an interface and its only implementation, where the shape is still being discovered).
- The split exists only to make tasks look tidy in the plan.

### Watch for the mis-sized task
The characteristic failure is sizing a **contract question** as mechanical work: "apply the code we already wrote to the second consumer" is not a copy if the two consumers disagree about what the contract *means*. If a task's real content is "decide what the rule is", size it as judgment work and expect a review.

## Phases

A phase is a group of tasks that together deliver an **observable slice** of the goal — something you could demo, or at minimum describe as "X now works".

Order phases so that:
1. Contracts, schema, and shared types come before their consumers.
2. Risky/uncertain work comes early — discover a wrong plan in phase 1, not phase 5.
3. Each phase leaves the repo shippable-ish (green build, green tests).

Typical shape: 2–6 phases, 2–6 tasks each. If you're producing 15 phases, you're planning too fine; if 1 phase with 20 tasks, you're not planning at all.

When you group tasks into a phase, note **where they touch each other** — the shared state, the shared key, the ordering dependency. That list is what the phase gate reviews.

## Writing the plan into the store

**When a detailed spec already exists, the plan is a thin task list that points at it.** A task's `intent` names the spec file and section; it does not copy them. Create top-down, one `work_create` call per level (`SKILL.md` › *Phase 0*, step 4).

| Plan element | Where it goes |
|---|---|
| Goal paragraph | the run's `intent` |
| Source of truth (the spec) | the run's `intent`, as a path — and in each task's `intent`, by section |
| Non-goals | `work_constrain(scope: run, kind: "domain_rule", text: "out of scope: …")` — it renders into every brief, which is where scope creep happens |
| Open assumptions | `work_assume(ref: <the item it affects>, statement, evidence, reversal, revisit_when)` |
| Phase — its observable slice, gate criterion and seams | `kind: "phase"`; the slice and gate criterion as `title` / `intent`, the seams in `intent` ("seams: none" is a valid answer and means the gate can be skipped), the phase-level suite as `verify_cmd` |
| Task — intent, likely files, done-criteria, verification | `kind: "task"`, `parent:` the phase ref; `intent` with the likely files; `criteria: [...]`; `verify_cmd` |
| "B cannot start before A is done" | `work_link(from: A, to: B, type: "blocks")` |
| A task that is review-required per the risk dial | `risk_tags: [...]` |
| Repo conventions, stack facts, safety rules, commands | `work_constrain`, once, scoped to the run — **never** in a task |

`work_create` **refuses a task with no `verify_cmd` or no done-criterion.** Do not satisfy it with a placeholder.

- **A verification command must be able to fail.** Write the filter against a class the task will *create*, not one that already exists — `--filter ~DatasetRun` matching eight passing tests reports `Passed! 8` against an endpoint nobody implemented. If you cannot name a command that goes red on unimplemented work, the done-criterion isn't real yet. `work_audit` lists the commands never recorded red — suspects, not convictions.
- **A superseded task is linked, not annotated.** A replaced task left open stays on the frontier. Create its replacement and `work_link(from: <new>, to: <old>, type: "supersedes")`, which marks the old one superseded. Never retitle an old task into a new one.

## Recording as you go — by exception

Every transition, verification and closure already lands in the journal (`work_history`). Write only the *why*:

- a deviation that changed the plan → `work_note(kind: "decision", from: <task>, body: …)`;
- an assumption with a non-trivial reversal cost → `work_assume`;
- a re-plan → the item changes themselves, plus one `work_note(kind: "decision")` saying what reality turned out to be;
- a failure, and what the next attempt changed → `work_verify(passed: false)` for the red run, and the change in the rework brief;
- something the next agent must not rediscover → `work_note(kind: "carry_forward" | "do_not_rederive", to: "<the item that needs it>")` — it renders into that item's brief exactly once.

**If the commit message already says it, don't repeat it in a note.** One line buys the same recovery as thirty.

## Re-planning

A re-plan trigger from `SKILL.md` › *Phase 1..N — The execution loop*, and a blocker from a plan audit, both land here.

1. Write what reality turned out to be — one `work_note(kind: "decision", from: "<run ref>", body: …)`.
2. Change the remaining work only; never rewrite the history of completed items. New tasks with `work_create`, re-ordering with `work_move` (ordering is fractional, so inserting between two tasks renumbers nothing), corrected fields and criteria with `work_update`, replaced tasks with `work_link(type: "supersedes")`, obsolete ones with `work_status(ref, "cancelled", reason: …)`. The task that triggered the re-plan closes with `resolution: "replanned"`.
3. **If a committed spec was proven wrong, add a task to fix the spec.** Fixing only the code leaves the team artifact lying to the next reader.
4. **If the revision reshaped a phase, audit it** — `SKILL.md` › *The plan audit*, `work_audit` first, then the agent. The fix round is the one most likely to leave a dependency on superseded work, a verification command copied from the old task, or the losing side of a decision alive in an intent. Re-scoping a single task doesn't earn one.
5. Tell the user what changed and why, briefly. Don't ask permission unless the scope or cost moved materially.
6. Resume the loop at `work_next`.
