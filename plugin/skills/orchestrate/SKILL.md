---
name: orchestrate
description: Execute a large plan, spec, or multi-phase undertaking by becoming an orchestrator that delegates every task to subagents (strictly sequential for anything that writes; parallel only for read-only recon), verifies each result against the high-level goal, and re-plans when reality diverges — keeping the main context clean. The plan, the rules, the evidence and the record live in Workman (the `work_*` MCP tools), which this skill requires. Use when asked to "execute this plan", "implement this spec/RFC end-to-end", "do this big refactor/migration/rebuild", or for any job too large to finish well in one context window.
---

# Orchestrate

For the rest of this undertaking you are the **orchestrator**, not the implementer. Your value is judgment, sequencing and verification. Your context window is the scarcest resource in the system: it is the only thing that holds the whole picture.

Invoking this skill is the user's explicit authorization to spawn subagents. You do not need to ask again per task.

---

## Preflight — before anything else

**Call `work_brief(repo: {remote, root})`.** A project is bound to its repository: `remote` is `git remote get-url origin` (omit it when there is no origin), `root` is the parent directory of `git rev-parse --path-format=absolute --git-common-dir` — the main checkout, the same from every worktree. A session launched by `agent_launch` already carries both, so a bare `work_brief()` finds the project too.

- **No `work_*` tools in your list** → tell the user in one line: *"orchestrate needs the Workman MCP server and this session cannot see it — run `/workman:setup`."* Then stop. There is no markdown fallback.
- **`work_brief` exists but `work_constrain` does not** → the server is older than this skill. Say so in one line, point at `/workman:setup`, and stop.
- **It says `session  unnamed — work_session(…)`** → `work_session(name:)` with the name `ListAgents` gives in its first line, *"This session is <name> [ref]"* (the name, not the ref).
- **It answers** → if it names a run in flight that is this undertaking ("continue orchestrating phase 4"), **resume the loop from its NEXT line**: Phase 0 and its approval gate do not re-fire. On an empty server it answers with onboarding; that is Phase 0.

---

## The role change

**You do not write product code.** Work reaches the repo only through worker subagents.

- *Sole exception:* a **zero-behaviour change** — a comment, a doc line, a typo in a string — that the existing verification already covers. Record it with `work_note(kind: "decision", from: "<task ref>", body: "orchestrator edit: <what>, <sha>")`. Anything that could change behaviour, needs a decision, or is the second occurrence in the same task → delegate a fixup task.

**Read to decide, not to learn.** Before any `Read`, ask what decision it changes. If none, skip it.

| Allowed | Not allowed |
|---|---|
| The plan / spec, `work_brief` / `work_get` / `work_tree`, worker reports | Reading source files "to get oriented" |
| Command output (build, test, typecheck) | Reading a file a worker already summarized |
| `git diff --stat`, `git status`, file lists | Reading full diffs |
| Targeted spot-checks that settle a specific doubt | Re-deriving what a report already told you |
| `AGENTS.md` / `CLAUDE.md` conventions, once | Exploring "while I'm here" |

A question about the code is a question for a subagent, not a `Read` call.

**The store is your memory.** Every decision, assumption, deviation, verification and finding goes into Workman the moment it happens. The test: if this session were killed now, a fresh orchestrator calling `work_brief` and nothing else must be able to continue.

### Full mode and light mode

**Full mode is the default**: ≥3 phases or ≥8 tasks, or anything that plainly exceeds one context window.

**Light mode** only when **all three** hold: the undertaking is small (a handful of tasks, one or two phases); it is contract-dense and seam-sensitive, so splitting it across contexts would itself create the bugs (an interface and its only implementation; a schema and the code that must match it); and you already hold the relevant context. In light mode you execute sequentially yourself and delegate only read-only recon; verification, red-checks, commit discipline and the store record are unchanged. Say once that you're running light and why.

When in doubt, run full. Never run writers in parallel in either mode.

---

## What things cost

| Action | Cost |
|---|---|
| Run a verify command · read a worker report · `git diff --stat` · a `work_*` call | ~free |
| Targeted spot-read to settle one doubt | cheap |
| Recon `Explore` agent | 10–30k |
| Worker task | 30–80k |
| **Review agent** | **60–150k** |
| **Plan audit** | **60–150k** |
| **Phase gate** | **100–150k** |

**A review costs more than an extra worker task.** Spend one only where a worker could not have proved the same thing itself — usually it could, with a red-check. A plan audit is priced against a phase executed wrongly and reworked.

---

## Phase 0 — Frame the undertaking

1. **Establish the goal.** A path to a plan/spec file → read it, that's the plan; prose → that's the goal statement; nothing → ask the user in one question.

2. **Write the goal in one paragraph**: what is true when this is done, and how a human would confirm it. Every later review measures against it. If you can't write it, ask.

3. **Spike before planning when the undertaking targets a system you don't control** — a live external service, a third-party API, a production database, someone else's schema. A read-only probe against the real system beats any assumption: calls that always throw, "successful" operations that do nothing, case-sensitive matching that returns the wrong row are invisible in code review.

4. **Create the run in the store.** One `work_create` call per level, top-down, reading the refs each call prints back before the next:
   - the **project**, only when `work_brief(repo: …)` says this repo is not bound to one — with `repo:` the same `{remote, root}`, exactly as its NEXT line prints it. If it belongs to a project that exists, bind it with `work_update(ref, repo:)` instead; never create a second project for a repo;
   - a **run** under it — `parent:` the project ref, `ref:` a short kebab slug, `intent:` the goal paragraph;
   - the **phases** (`parent:` the run ref), then the **tasks** (`parent:` their phase ref), per `references/decompose.md`.

   Then write the run's **binding rules** — safety rules, stack facts, verification commands, hazards — once, with `work_constrain(scope: "<run ref>", kind: …, text: …)`. They render into every brief in the run, so no brief restates them. A `red_line` must carry a `route:` — what to do instead, or who to ask.

   Settle version control now: confirm the working tree is clean (dirty → stop and ask; don't sweep unrelated changes into task commits), check `git log -5` for commit conventions, and create a working branch if on the default branch.

5. **Decompose** into phases → tasks, per `references/decompose.md`. **When a detailed spec exists, tasks point at it** by file and section, never restate it. Record every open assumption with `work_assume`, every ordering dependency with `work_link(type: "blocks")`.

6. **Gate: present the plan and get approval before the first writing agent runs.** Show `work_tree(scope: "<run ref>")` — phases, tasks, the verification command each will be judged by — and the open assumptions. Then stop and wait. This is the only mandatory user gate; after it, run to completion unless an escalation trigger fires.

   **A run planned by `/workman:plan`** is already in the store and, once the human says *"approved, execute as /workman:orchestrate"*, already approved. Skip steps 4 and 5, record `work_note(kind: "decision", from: "<run ref>", body: "plan approved by <human>: execute as orchestrate")`, run the plan audit on it, and start Phase 1. Ask again only if the audit finds a defect that changes the plan.

   **If the plan is not yours** — someone else's spec, a handoff document, a plan from an earlier session — run a **plan audit** before this gate.

**Recon is just-in-time**: at the start of the phase that needs it, never more than one phase ahead. Up to 4 read-only `Explore` agents in parallel, one disjoint question each, ≤15-line answers with file paths. Run it **even when the plan names the files** if the plan predates commits on this branch or targets a system you don't control — stale `file:line` references read as confidence.

---

## Phase 1..N — The execution loop

For each task, in the order `work_next` gives. **Writing agents run one at a time. Never two in parallel.** (Only exception: genuinely disjoint file sets *and* `isolation: "worktree"` — and even then, prefer sequential.)

```
next → claim → brief → delegate → report → verify → judge → commit → close → next
```

**1. Next and claim.** `work_next(scope: "<run ref>")` names the ready task and its command. `work_status(ref, "in_progress")` claims it.

**2. Brief.** Use the template in `references/briefs.md`. The done-criteria, verification command and BINDING block come from `work_brief(scope: "<task ref>", for: "worker")` — hand those over verbatim. The brief adds what the store cannot give: the allow-list, the banned shell shapes, the facts you established, the environment's benign noise.

**3. Delegate.** One `general-purpose` agent, `run_in_background: false`. Give it the *why*: a worker that understands the goal makes better local calls.

**4. Report.** The worker returns the compact report (STATUS / FILES / VERIFY / RED-CHECK / DEVIATIONS / ASSUMPTIONS / RISKS / NOT DONE) — **no code, no diffs**; if it returns code, re-ask for the format. Record it with `work_report(ref, status, files, verify, red_check, deviations, assumptions, risks, not_done)` — each `assumptions` line becomes an open assumption.

**Read `DEVIATIONS` closely.** It is where you learn your own brief was wrong — the method has no production caller, the sort order makes a test unfalsifiable, the component doesn't do X. It is something to read, not a trigger to auto-escalate into a review.

**5. Verify** — see below. Every run you make is recorded with `work_verify`.

**6. Judge — one of four verdicts:**
- **Accept** — verification green, review clean (or correctly skipped), result serves the goal paragraph.
- **Rework** — specific, bounded gap. Spawn a *fresh* worker with a narrow brief naming exactly what's wrong; never continue the old agent for a correctness failure. The task stays `in_progress` and closes later with `resolution: "reworked"`.
- **Re-plan** — the failure is in the task, not the execution.
- **Escalate** — see escalation rules. `work_status(ref, "blocked", reason: …)` while you wait.

**7. Commit** — see *Commit discipline*.

**8. Close.** Tick each criterion with its evidence — `work_update(ref, criteria: [{id: "c1", checked: true, evidence: "…"}])` — then `work_status(ref, "done", commit: "<sha>")`. `force: true` leaves a permanent mark; use it only when you can say why in `reason:`, never because the guard is inconvenient.

**Re-plan triggers** (stop, revise the plan in the store, tell the user what changed and why):
- Two attempts at a task that varied the *same dimension* both failed.
- A worker's report reveals the codebase doesn't work how the plan assumed.
- A task's real scope turns out >2× its estimate.
- A phase gate finds the phase delivers something other than its intended slice.
- A plan audit returns a blocker against a phase not yet run.

Re-planning is a success mode. Mechanics: `references/decompose.md` › *Re-planning*.

---

## Verification

### Always: run the command yourself

Never trust a worker's "tests pass". Also confirm the changed-file list matches the report — unreported files are a finding.

**Record every run, red ones included:** `work_verify(ref, result_line: "<the line it printed>", passed: true|false)`. **Never let a subagent call `work_verify` or `work_status`:** it runs on your session's credentials, so its claim would be recorded as your evidence.

- **Run the narrowest command that can fail for this task.** Filtered tests during tasks; the full suite only at phase gates and close-out.
- **A check that could not have come out the other way verifies nothing.** A measurement needs ≥3 runs — record median and range. A setup the system silently rejected makes a driven check a false defect — read the state back before believing a red.
- **Never re-confirm an already-green state.**
- **Read the real output line, not the exit code.** An `exit 0` at the end of a pipe belongs to the `tail`. The `Built …` / `N passed` line, verbatim, is the `result_line`.
- **Run the real thing** when the work is environment-specific: production-only code paths, container images, device artifacts, anything visual. Green local tests don't prove an image boots past a Production-only guard.
- **For a live-system symptom, read the live logs first, before theorizing.**
- **`git log -1` right after the first worker returns** — catch a worker that committed or amended before you stack on it.

### Red-checks — the cheapest proof in the system

**Any test that defends a fix, or backs a "behaviour preserved" claim, must be confirmed failing against the un-fixed code, with the failure count.** It goes in `red_check:` on `work_verify`. A test written after the fix, against the fixed code, looks exactly like one that works.

A red-check proves only that the test fails against the one bug you fixed, not that the fixture discriminates the rule. **Make the worker name the wrong implementations its fixture still passes, and defeat those.** A test that passes against the broken code is deleted, not kept. This runs in the worker's disposable context; a review catching the same defect costs 60–150k.

### The risk dial — when to spend a review agent

**Review required** when the task touches:
- auth, tenancy/isolation, permissions, secrets, or money;
- schema, data migration, or anything deleting data;
- a **published contract** — API/wire shape, event, generated-client surface, integrator-facing docs;
- a **shared identity or resolution rule** — how things are named, matched, keyed, canonicalized, or deduped;
- an **equivalence claim** — any "no behaviour change" restructuring (the worker's tests were written against the new code, so they prove nothing);
- a report admitting a near-miss, or a premise that shifted under the task.

Mark such a task with `risk_tags: [...]` on `work_create` or `work_update`, so `work_audit` can tell you which tags no constraint covers.

**Budget never overrides this list.** If you can't afford the review, you can't afford the task.

**Review skipped** — each with a free signal:
- test-only tasks;
- **additive-only**: `git diff --stat` shows zero modifications to tracked files;
- mechanical deletion where the compiler is the oracle;
- the changed behaviour is fully covered by a **red-checked** test;
- ≤3 files, mechanical, verification green, no deviations — spot-check the important hunk and accept.

**Not** triggers: file count, and "the report lists a deviation or assumption" (every good report does).

Keep reviews cheap: explicit diff scope, no whole-repo exploration, ≤5 findings — see `references/briefs.md`. Record them with `work_finding(findings: [{target: "<task ref>", title, severity: "blocker"|"minor", source: "review"}])`; a blocker holds the task open.

### The phase gate — a composition review

The gate answers the one question per-task review cannot: *these N pieces are each correct — where do they meet, and what breaks at the seam?* Name the seam classes in the gate brief: shared state, ordering and timing, identity and keys, lifecycle, and contracts crossing task boundaries.

Before the gate, run `work_threads(scope: "<phase ref>")` and `work_audit(scope: "<phase ref>")`; hand their output over as established and spend the gate's budget on the seams. Gate findings: `work_finding(…, source: "gate")` against the task or phase they break. The phase closes like a task: the full suite recorded with `work_verify` against the phase ref, then `work_status(phase ref, "done")`.

- **Skip the gate when the phase has no seam** — a single task, or tasks sharing no state and no contract. Record `work_note(kind: "decision", from: "<phase ref>", body: "gate skipped — no seam")`.
- **Re-gate after gate-produced fixes that touched the seam.**
- **When a gate proves a committed spec wrong, fixing the spec is a task.**

### When verification is interrupted

If an agent dies or a limit is hit, look for evidence that is already structural (a test that cannot pass against the old code) before re-running anything. Cite it, and say plainly that the direct check didn't run.

---

## The plan audit

A fresh read-only agent pointed at the phases **not yet executed**, asked what would make them fail. The phase gate looks back at completed tasks for seams; the plan audit looks forward at a plan written before the code existed. When a brief is wrong, every other control passes it through — including your own verification, because you run the command the plan told you to.

**Run `work_audit(scope: "<run ref>")` first; it is free.** It finds verification commands never recorded red, items closed without an orchestrator verification, dependency inversions, open work pointing at superseded items, risk tags no constraint covers, and red lines with no route. Hand its output to the audit agent as established.

### When to run one

Any of these; never twice on the same unchanged plan:

- **The plan came from somewhere else** and you are about to approve it in Phase 0.
- **Two phases have completed since the last audit** (or Phase 0) and **≥2 phases remain** — the routine cadence.
- **You re-planned and the revision reshaped a phase.** Re-scoping one task doesn't earn an audit.
- **A gate blocker's real cause was in the plan.**
- **The next phase has high blast radius** — schema or migration, auth or tenancy, a published contract, production.
- **Before a handoff** to a fresh context — the question is the same: *could a competent stranger execute this from `work_brief` alone?*

Skip it when ≤1 small phase remains, when nothing changed or completed since the last audit, or in light mode. Record each audit and its verdict with `work_note(kind: "decision", from: "<run ref>", body: "plan audit after phase <n>: <verdict>")` — that note is how the next orchestrator computes when the next one is due. A clean audit is a common, legitimate result.

### What it checks

The brief is in `references/briefs.md`. Ranked by yield:

1. **Could each remaining verification command fail?** The agent *runs* them. A filter that already matches passing tests (`--filter ~DatasetRun` → `Passed! 8` against an unimplemented endpoint) gets the task accepted unimplemented, and your own re-run confirms it. Highest-yield check, invisible to every other control.
2. **Does everything the remaining tasks depend on exist, or get built by an earlier task?**
3. **Does anything still point at superseded work?**
4. **Are the plan's load-bearing facts still true?** Name the claims the design rests on and what happens if one is false.
5. **Does the plan contradict its spec, or itself?**
6. **Do the run's constraints still cover what the upcoming tasks can destroy?**
7. **Ordering** — including contract inversions, not just sequence.
8. **Shape** — given what the completed phases taught. Ask for it explicitly: *abstract first, then deeper*.

### What to do with the result

Record findings with `work_finding(…, source: "plan_audit")`. Blockers change the items (`work_update`, `work_create`, `work_link`) or the rules (`work_constrain`) before the next task runs — that is a re-plan. Minors stay open until closed or deferred. When an audit becomes tasks, `work_move` each finding under the task that will fix it, and resolve it when that task closes.

**Re-audit after fixing blockers by editing the plan** — fix rounds drop lines and leave decorative filters standing. **Two not-ready verdicts on the same axis means the shape is wrong**: escalate rather than audit a third time.

---

## Commit discipline

Invoking this skill authorizes committing. **The orchestrator commits; workers never do.** Commit only after verification passed and the verdict is *Accept*.

- **One accepted task = one commit**, by default. Never let two tasks accumulate in the tree. **Gate-produced fixes batch into one commit per gate round.** One commit per phase is the floor; one for the whole undertaking is always wrong.
- **The sha goes on the close:** `work_status(ref, "done", commit: "<sha>")`.
- **Stage deliberately.** `git add` the files the worker reported, never `git add -A`. Files nobody reported are a finding — investigate before committing. Grep staged content for secrets before every commit.
- **Never push.** Push and PR creation stay the user's call unless they ask.
- **Never `--no-verify`.** A failing hook becomes a rework task.
- **Message format** (match the repo's conventions from Phase 0):
  ```
  <type>(<scope>): <what this task delivered>

  <1–3 lines: why, and any assumption taken>
  Task: <task ref> of <run ref>

  Co-Authored-By: Claude Opus 5 (1M context) <noreply@anthropic.com>
  ```

---

## Critical thinking — with a hard ceiling

Challenge the plan and worker output, and **ship**. These budgets are binding:

**Evidence budget.** Any single open question gets at most **1 recon agent, or 3 targeted searches, or 5 file reads**. Still open → `work_assume(ref: "<item it affects>", statement, evidence, reversal, revisit_when)`, choose the **cheapest-to-reverse** option, proceed. Re-opening a settled question requires new evidence.

**Change what varies.** Two failed attempts varying the *same dimension* → split it, change the approach, or escalate. A third attempt that closes the previous findings and varies a different axis is convergence, not thrash.

**Reversibility test.** *If this is wrong, what does the fix cost?* Cheap (naming, local structure, a helper's shape) → decide in seconds. Expensive (schema/migrations, public API and wire contracts, auth/tenancy boundaries, new dependencies, data deletion) → deliberate there, and escalation is legitimate.

**No speculative breadth.** Ask for more evidence only when you can state what would change if the answer went the other way.

**Reviews judge against criteria, not taste.** Scope: the task's done-criteria plus the goal paragraph. A finding names a concrete failure: *input/state → wrong behaviour*. Cap 5, ranked; only blockers gate.

**No gold-plating.** No adjacent refactors, no abstraction with fewer than three call sites, no new dependency without user approval.

**Batch the trivia.** Four small mechanical fixes are one brief, not four agents.

---

## Escalate to the user when — and only when

- A decision is expensive-to-reverse, unresolved after its evidence budget, and proceeding either way could waste a phase.
- A re-plan changes the goal's scope or cost materially.
- Something destructive is required (data migration, deletions beyond the task's scope, force-push, touching production). **This includes a worker proposing to delete something outside its brief** — it does not know what a later phase needs.
- The plan's premise is contradicted by the code and you'd be guessing at intent.

One clear question with a recommendation. Everything else you decide yourself, record, and proceed.

---

## Closing out

1. Full-repo validation: build + typecheck + full test suite, run by you and recorded with `work_verify` against the run.
2. `work_threads(scope: "<run ref>")` and `work_audit(scope: "<run ref>")`. Resolve each assumption, close or defer each finding and follow-up. No open thread nobody decided about.
3. One final review agent: given the diff scope and the goal paragraph, what's missing?
4. Close the run: `work_status(run ref, "done")`.
5. **Retro — improve this skill**, only if the run earned it: a rework, a re-plan, a red verification, a gate blocker, or one traceable ≥10k-token waste caused by this skill's guidance. Otherwise one skip `work_note`, done. When it fires, follow `references/retro.md` (noise floor, duplicate search, issue schema, push-access gate). Build it from what you already hold, plus **at most one read: the skill file you propose to edit**.
6. Report to the user: what was built, what was verified (actual command results), assumptions taken, deferred follow-ups, anything left out and why. If something failed, say so with the output — do not round up to done.

---

## Reference files

Read these when you reach the relevant step; don't preload them.

- `references/decompose.md` — task sizing, phase structure, writing the plan into the store, re-planning
- `references/briefs.md` — worker, reviewer, gate and plan-audit briefs, report contract, recon rules, test policy
- `references/retro.md` — close-out retrospective: noise floor, dedupe, and the GitHub issue schema
