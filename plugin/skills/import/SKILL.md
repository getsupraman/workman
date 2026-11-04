---
name: import
description: "Import a repo's existing planning material into Workman: find plan files, TODO lists, roadmaps, specs, ADRs, docs and agent rules anywhere in the repo, judge each against the CURRENT code, keep only what is still true (open work, binding decisions and rules, open assumptions), propose a per-file plan, ask the human, then write it with work_import into the repo's project — with source and reason on every record, idempotent by source. Writing needs a session launched with the Workman import grant; an ordinary session offers to launch one and hands the import to it. USE FOR - bringing a repo that predates Workman under it; re-running an import after the plans changed; turning PLAN.md / TODO.md / ADR folders into runs, tasks, followups, rules and assumptions. DO NOT USE FOR - recording history or finished work (it is not imported), editing or moving the source files (never), or work you are planning now (use work_create via /workman:orchestrate)."
---

# Workman import

You are turning a repo's planning material into Workman records. There is no parser: **you** read
the material, **you** check it against the code, and **you** decide what is worth keeping. The tool
only writes what you hand it.

**The import rule — the only one that matters.** A record is worth importing only if it is true of
the code **as it is now**: open work that still applies, a decision or rule that still binds, an
assumption still open, a fact the code bears out. History is never imported — "used to", "phase 2
did", "we migrated from", a closed phase's report, a superseded plan, a TODO the code already
satisfies. It is a present-tense rule, applied to plans: if the sentence
describes the past, or a future the code has already reached, it is dropped.

---

## 1. Preconditions — stop at the first that fails

1. **`work_*` tools present.** None → *"/workman:import needs the Workman MCP server — run
   `/workman:setup`."* Stop.
2. **`work_import` present.** It is shown only to a session started for an import (a session token
   minted with the `import` grant); an interactive session signed in as the human never has it, by
   design: bulk writes with provenance belong to a session launched for exactly that. Absent → this
   session hands the import to one it launches. Tell the human in plain words, then offer:
   *"This session can't write an import — Workman shows the import tool only to a session started
   for one. Shall I start that session for this repo with your request? It runs in the background
   and asks you before it writes anything."*
   On a yes, call `agent_launch` with `cwd` the repo root, `workman: {role: "orchestrator", grants:
   ["import"], url: <this session's Workman server origin>}` (omit `url` for a local server on
   127.0.0.1:5290), and a prompt that is `/workman:import` followed by the human's own arguments
   verbatim. Refused with *not signed in* → `agent_signin {"url": …}` (it opens the browser), then
   launch again. Then follow `/workman:agents` to watch it and relay its plan to the human. No
   `agent_launch` tool either → tell the human to install the plugin's agents server (`/workman:setup`)
   and stop. Never work around a missing `work_import` with `work_create` — provenance and
   idempotency live only in `work_import`.
3. **The repo's project.** Call `work_brief(repo: {remote, root})` — `remote` is
   `git remote get-url origin` (omit with no origin), `root` the parent of
   `git rev-parse --path-format=absolute --git-common-dir`. A session from `agent_launch` already
   carries both. If it names no project, or the project is bound to no repo, **ask the human** which
   project this repo is, then create it (`work_create(items:[{kind:"project", …, repo:{remote, root}}])`)
   or bind it (`work_update(ref, repo:{…})`). `work_import` refuses an unbound project.

## 2. Discover — everything, everywhere

Planning material is not only in `.claude/orchestration/`. Search the whole repo:

- `.claude/orchestration/**`, `.claude/**/*.md`, `PLAN*.md`, `LEDGER*.md`, `TODO*`, `ROADMAP*`,
  `BACKLOG*`, `CHANGELOG*` (its `[Unreleased]` only — released sections are history)
- `docs/**`, `spec*/**`, `rfc*/**`, `adr*/**`, `decisions/**`, `design/**`
- issue lists and checklists in any `*.md` (`- [ ]`), `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md`,
  `.cursorrules`, `.github/copilot-instructions.md`
- `TODO` / `FIXME` comments only when a plan file points at them

Exclude `node_modules`, `bin`, `obj`, `dist`, `build`, `vendor`, `third_party`, `.git`, generated code
and lockfiles. Use Glob and Grep, not shell pipelines. **List the candidates** — path, size, last
commit date (`git log -1 --format=%cs -- <path>`) — before reading any of them. An old date is a
reason to look harder, not a verdict.

## 3. Judge each candidate against the current code

For every claim a file makes, **read the code it talks about** and assign one verdict:

| Verdict | When | Becomes |
|---|---|---|
| **import** | open work the code does not yet do; a rule or decision the code still follows; an assumption nothing has settled | `run` / `phase` / `task` / `followup`, `rule`, `note`, `assumption` |
| **drop — done** | the code already does it | nothing; say which file/symbol shows it |
| **drop — history** | narration of what was, what a phase did, why something changed | nothing |
| **drop — superseded** | a later plan, ADR or the code contradicts it | nothing; name what supersedes it |
| **ask** | you cannot tell from the code, or two sources disagree | a question to the human |

**A living contract is referenced, never copied.** `AGENTS.md`, `CLAUDE.md`, `CONTRIBUTING.md`, agent
rule files and a spec the team keeps current are already loaded or read by every agent; a copy of their
rules in Workman goes stale the first time the file changes, and every brief pays for it. Do not turn
them into `rule` records. What they *lack* is still yours: a gap between the contract and the code (a
stale count, a promise the code does not keep) is a **followup** with the contract as its `source`.

Shape what you keep:
- A coherent body of work is a **run**; its stages **phases**; its units **tasks** only when you can
  name a `verify_cmd` that can fail and at least one done-criterion — otherwise a **followup**
  (`work_import` refuses a task without them, exactly as `work_create` does).
- A standing rule is a **rule**; give it `route` (what to do instead) when it is a prohibition — that
  makes it a red_line. A decision a later agent must not re-derive is a **note**. An unverified
  belief the work rests on is an **assumption**.
- Give every item a readable kebab-case `ref` (`repo-map-stale`, `pg-backup`) — a numbered ref like
  `1` collides across runs and says nothing in a brief.
- Every record carries `source: {path, anchor}` — repo-relative path, anchor a heading text or
  `L<line>` — and `why`: one line naming what in the code bears it out. Two records never share a
  source; the source is the key a re-import updates by.
- `status` is `open`, `ready` or `blocked`. There is no imported `done` and no imported evidence.

## 4. Propose, then stop for the human

Write the plan per file: **import → which records** / **drop → why** / **ask → the question**. Then
run the batches with `work_import(…, dry_run: true)` and show the human the table it returns — that
is the exact effect. **Stop and wait for their yes.** Answer the asks before importing anything they
touch.

## 5. Import

`work_import(project, items:[…])`, one batch per run (with its phases, tasks, followups, notes, rules
and assumptions), in dependency order — a row may name as `parent` a ref created by an earlier row
of the same batch. A refusal writes nothing; fix what it names and resend. Re-running is safe: a
known source updates its record, and an item someone has moved since is reported `skipped: moved
since import` and left alone.

## 6. Check and report

`work_tree(scope: <project>)` and `work_get(refs:[…])` against your plan — every kept record present,
under the right parent, with its source and why. Then report to the human: created / updated /
skipped (with refs and sources), and the **dropped material with a reason for each file**.

**Never modify, move or delete a source file.** The import reads the repo; it never edits it.

## 7. Large repos

One nested agent per area (a service, a docs tree, an orchestration folder): `agent_launch` each with
`workman: {role: "orchestrator", grants: ["import"], …}` and a brief naming its area and this
skill. Each agent proposes, gets the human's yes through you, and imports its area. You check each
area's `work_tree` against its plan before the next starts.
