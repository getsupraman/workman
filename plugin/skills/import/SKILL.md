---
name: import
description: "Import a repo's existing planning material into Workman: find plan files, TODO lists, roadmaps, specs, ADRs, docs and agent rules anywhere in the repo, judge each against the CURRENT code, keep only what is still true (open work, binding decisions and rules, open assumptions), propose a per-file plan, ask the human, then write it with work_import into the repo's project — with source and reason on every record, idempotent by source. Writing needs a session launched with the Workman import grant; an ordinary session offers to launch one. USE FOR - bringing a repo that predates Workman under it; re-running an import after the plans changed; turning PLAN.md / TODO.md / ADR folders into runs, tasks, followups, rules and assumptions. DO NOT USE FOR - recording history or finished work (it is not imported), editing or moving the source files (never), or work you are planning now (use /workman:plan or /workman:orchestrate)."
---

# Workman import

There is no parser: **you** read the material, check it against the code, and decide what to keep.
The tool only writes what you hand it.

**The import rule.** A record is worth importing only if it is true of the code **as it is now**:
open work that still applies, a decision or rule that still binds, an assumption still open, a fact
the code bears out. History is never imported — "used to", "phase 2 did", "we migrated from", a
closed phase's report, a superseded plan, a TODO the code already satisfies.

---

## 1. Preconditions — stop at the first that fails

1. **`work_*` tools present.** None → *"/workman:import needs the Workman MCP server — run
   `/workman:setup`."* Stop.
2. **`work_import` present.** It is shown only to a session minted with the `import` grant; an
   interactive session signed in as the human never has it. Absent → offer:
   *"This session can't write an import — Workman shows the import tool only to a session started
   for one. Shall I start that session for this repo with your request? It runs in the background
   and asks you before it writes anything."*
   On a yes, write a brief file under the repo's `.claude/briefs/` that is `/workman:import`
   followed by the human's own arguments verbatim, and call `agent_launch` with `repo_root` the repo
   root, that `brief`, your `controller` address, and `workman: {role: "orchestrator", grants:
   ["import"]}`. Refused with *not signed in* → `agent_signin()` (it opens the browser), then launch
   again. Then follow `/workman:agents` to watch it and relay its plan to the human. No
   `agent_launch` tool → tell the human to install the plugin's agents server (`/workman:setup`)
   and stop. Never work around a missing `work_import` with `work_create` — provenance and
   idempotency live only in `work_import`.
3. **The repo's project.** Call `work_brief(repo: {remote, root})` — `remote` is
   `git remote get-url origin` (omit with no origin), `root` the parent of
   `git rev-parse --path-format=absolute --git-common-dir`. A session from `agent_launch` already
   carries both. If it names no project, or the project is bound to no repo, **ask the human** which
   project this repo is, then create it (`work_create(items:[{kind:"project", …, repo:{remote, root}}])`)
   or bind it (`work_update(ref, repo:{…})`). `work_import` refuses an unbound project.

## 2. Discover — everything, everywhere

Search the whole repo:

- `.claude/orchestration/**`, `.claude/**/*.md`, `PLAN*.md`, `LEDGER*.md`, `TODO*`, `ROADMAP*`,
  `BACKLOG*`, `CHANGELOG*` (its `[Unreleased]` only)
- `docs/**`, `spec*/**`, `rfc*/**`, `adr*/**`, `decisions/**`, `design/**`
- checklists in any `*.md` (`- [ ]`), `CLAUDE.md`, `AGENTS.md`, `CONTRIBUTING.md`,
  `.cursorrules`, `.github/copilot-instructions.md`
- `TODO` / `FIXME` comments only when a plan file points at them

Exclude `node_modules`, `bin`, `obj`, `dist`, `build`, `vendor`, `third_party`, `.git`, generated code
and lockfiles. Use Glob and Grep. **List the candidates** — path, size, last commit date
(`git log -1 --format=%cs -- <path>`) — before reading any. An old date is a reason to look harder,
not a verdict.

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
rule files and a spec the team keeps current are already read by every agent; a copy in Workman goes
stale. Do not turn them into `rule` records. A gap between the contract and the code (a stale count,
a promise the code does not keep) is a **followup** with the contract as its `source`.

Shape what you keep:
- A coherent body of work is a **run**; its stages **phases**; its units **tasks** only when you can
  name a `verify_cmd` that can fail and at least one done-criterion — otherwise a **followup**.
- A standing rule is a **rule**; give it `route` (what to do instead) when it is a prohibition — that
  makes it a red_line. A decision a later agent must not re-derive is a **note**. An unverified
  belief the work rests on is an **assumption**.
- Give every item a readable kebab-case `ref` (`repo-map-stale`, `pg-backup`), never a number.
- Every record carries `source: {path, anchor}` — repo-relative path, anchor a heading text or
  `L<line>` — and `why`: one line naming what in the code bears it out. Two records never share a
  source; the source is the key a re-import updates by.
- `status` is `open`, `ready` or `blocked`. There is no imported `done` and no imported evidence.

**Every record stands on its own.** The planning files may be deleted after the import, so `title`,
`intent`, `criteria` and `body` inline the facts, numbers, URLs, code paths, measurements and
reasoning the record needs. `source` is provenance and the re-import key only. "See HANDOFF §3",
"per P4-plan.md §4.5" or "as described above" is a hollow record: copy what that section says (only
the part still true) instead. References are allowed only to a living contract that stays in the
repo, and to code (`path`, symbol).

On a re-import, rewrite any record of the project whose text still points into a planning file: send
it again under its own source with the content inlined. One reported `skipped: moved since import`
is fixed with `work_update(ref, intent:…, criteria:[…])` instead.

## 4. Propose, then stop for the human

Write the plan per file: **import → which records** / **drop → why** / **ask → the question**. Then
run the batches with `work_import(…, dry_run: true)` and show the human the table it returns — that
is the exact effect. **Stop and wait for their yes.** Answer the asks before importing anything they
touch.

## 5. Import

`work_import(project, items:[…])`, one batch per run (with its phases, tasks, followups, notes, rules
and assumptions), in dependency order — a row may name as `parent` a ref created by an earlier row
of the same batch. A refusal writes nothing; fix what it names and resend. Re-running is safe: a
known source updates its record, and an item moved since is reported `skipped: moved since import`
and left alone.

## 6. Check and report

`work_tree(scope: <project>)` and `work_get(refs:[…])` against your plan — every kept record present,
under the right parent, with its source and why. Then ask of every record: **would it survive its
source file being deleted?** One that needs a planning file, a section of one, or "above"/"below"
fails — inline the missing content and send it again. Then report to the human: created / updated /
skipped (with refs and sources), and the **dropped material with a reason for each file**.

**Never modify, move or delete a source file.** Once the check passes, say which planning files are
now fully held in Workman: the human, or a controller they ask, may delete them. Living contracts stay.

## 7. Large repos

One nested agent per area (a service, a docs tree, an orchestration folder): `agent_launch` each with
`workman: {role: "orchestrator", grants: ["import"]}` and a brief naming its area and this skill.
Each agent proposes, gets the human's yes through you, and imports its area. Check each area's
`work_tree` against its plan before the next starts.
