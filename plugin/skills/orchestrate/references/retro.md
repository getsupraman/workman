# Retro — how this skill improves itself

**This file is the contract**: when the orchestrator files a retrospective issue, what it must contain, and the commands to file it. The repo command that later applies those issues reads the schema from here.

The subject of a retro is **this skill**, never the undertaking. "The migration was harder than planned" is not a finding. "`references/briefs.md` let a worker ship an unverified claim" is.

---

## The retro must be cheap

It is built **only** from what you already hold:

- the run's record in the store — `work_history(scope: "<run ref>")` for the closures, resolutions and verifications, `work_threads(scope: "<run ref>")` for what was left open;
- the worker and reviewer reports you already read;
- the verification output you already ran.

**Do not** re-read source files, re-run recon or verification, spawn a review agent, or re-read worker output you summarized.

*One read is permitted:* the single skill file you are proposing to edit, to quote its current text verbatim — skip it when the file is already in context. If you can't afford that read, file nothing; a proposal the consumer can't apply is worse than silence.

Target cost for the whole step: **under ~2k tokens**.

---

## Noise floor — skipping is the default

**Run the retro only if at least one of these is true, read straight off the run's journal (`work_history(scope: "<run ref>")`):**

| Trigger | Where it shows |
|---|---|
| ≥1 task closed `reworked` | a `done` transition with that resolution |
| ≥1 task closed `replanned` | a `done` transition with that resolution |
| ≥1 verification came back red and needed chasing | a verification recorded `passed: false` |
| A phase gate returned a blocker | a `blocker` finding with source `gate` |
| A single traceable waste of **≥10k tokens** caused by this skill's guidance | your decision notes / your own recollection of the run |

Otherwise **skip entirely**. No issue, no comment, no prose — one note: `work_note(kind: "decision", from: "<run ref>", body: "Retro: skipped — no reworks, no re-plans, no failed verifications.")`. Then stop.

**At most one issue per run.** Pick the single highest-value finding; two findings that share a root cause are one finding.

This mechanism fails by filing a plausible issue after every run until the skill accumulates advice that costs more than it saves. If you find yourself reaching for a trigger to justify an issue you already want to file, file nothing.

---

## What counts as a finding

A finding is a **concrete edit to a named section of this skill**. It must state all four:

1. **Symptom** — what actually went wrong this run, in outcome terms.
2. **Target** — the file *and* section heading in the skill that permitted it.
3. **Proposed edit** — the literal before/after text.
4. **Token delta** — signed, and honest.

If you cannot name the section, you do not have a finding — you have an observation about the undertaking. Drop it.

| Not a finding | A finding |
|---|---|
| "Reviews cost a lot." | "`SKILL.md` › *The risk dial* lists six review triggers; the equivalence-claim one fired on a doc-only task. Narrow it to code." |

### Token delta, and the bias against adding

State the delta on every proposal. **Prefer replacing or tightening existing text over appending new text.**

- `-N` or `±0` — the good case: the sentence was too weak, in the wrong file, or in a section not read at that step.
- `+1..+60` — acceptable if you can say why tightening existing text cannot do it.
- `+60` or more — **do not file it.**

A proposal that reads like general wisdom rather than a correction of a specific sentence is advice, not a fix — drop it.

Mark the proposal's mode explicitly: `replace`, `tighten`, `move`, or `add`. `add` is the one that needs an argument.

---

## Safety — post only where you can push

This skill lives in `getsupraman/workman`, a public repo most installers cannot write to, and you are usually running inside a private one. Posting is gated on capability, not on asking. Check it once, before any write:

```bash
gh repo view getsupraman/workman --json viewerPermission --jq .viewerPermission
```

- **`ADMIN`, `MAINTAIN` or `WRITE`** → **post automatically, no confirmation prompt.** That is deliberate: do not add one, and do not ask "shall I file this?" — either the triggers fired and you file, or they didn't and you skip.
- **Anything else** (`READ`, `NONE`, blank), or `gh` is absent, unauthenticated, or the command errors → step 5: write it locally and post nothing. A normal outcome — do not retry, escalate, or fall back to any other repo.

**Every retro filed there is public.** Either way — the local file is meant to be pasteable upstream — the body carries **findings and record excerpts only**:

- **Allowed:** journal events, resolutions, verify commands and their result lines, worker-report lines (`DEVIATIONS`, `RED-CHECK`), and verbatim text from this skill's own files.
- **Never:** source file contents, diffs, code from the repo being worked on, credentials, tokens, connection strings, absolute paths outside the repo, host names of private systems, customer or client names, anything from a private codebase.

Redact the part and keep the shape (`… tenant `<redacted>` …`), or pick different evidence. Then read the body once as a stranger: if it still identifies the engagement, cut more.

---

## Procedure

### 0. Check the noise floor

Read the run's journal. If no trigger fired, write the skip note and stop — before the access check.

### 1. Check access, then bootstrap the label

Run the `viewerPermission` check above. Not `ADMIN` / `MAINTAIN` / `WRITE` → skip straight to step 5.

On the push path only, create the label once per repo:

```bash
gh label create orchestrate-retro \
  --repo getsupraman/workman \
  --description "Self-proposed edit to the orchestrate skill, filed by its own retro" \
  --color 5319E7
```

*Already exists* is the expected steady state — do not retry, do not add `--force`. **`HTTP 403`** → do not continue to `gh issue create`; take step 5.

### 2. Search for an existing issue with the same root cause

**Required before creating anything.** `--state all`, not `--state open`: a rejected proposal is closed, and an open-only search would re-file it every run.

```bash
gh issue list \
  --repo getsupraman/workman \
  --label orchestrate-retro \
  --state all \
  --limit 50 \
  --json number,title,body,state,stateReason \
  --jq '.[] | "#\(.number)\t\(.state)\t\(.stateReason)\t\(.title)"'
```

Match on **root cause, not wording** — the same `## Target` (same file *and* same section) is a match even if the symptom looked different. Pull the full body of a candidate with `--jq '.[] | select(.number==<n>) | .body'` on the same command rather than a second fetch. Three outcomes:

| Matching issue | What you do |
|---|---|
| **`OPEN`** | Comment on it — do not open a near-duplicate. See below. |
| **`CLOSED` / `NOT_PLANNED`** — rejected | **File nothing and comment nothing.** The owner judged this proposal and stated a reason. One `work_note(kind: "decision", from: "<run ref>", body: "Retro: skipped — #<n> already rejected (<reason>).")`. Then stop. |
| **`CLOSED` / `COMPLETED`** — applied | A genuine recurrence: the edit landed and the failure happened anyway. File a new issue (step 3), and say in `## Symptom` that #`<n>` was applied and did not hold. |

An **open** match — comment:

```bash
gh issue comment <number> \
  --repo getsupraman/workman \
  --body-file .claude/retro/<run ref>/retro-comment.md
```

Comment body, exactly:

```markdown
### Recurrence — <run ref>, <YYYY-MM-DD>
<One line: how it manifested this run.>
Evidence: <one journal event or verify line>
Proposal: unchanged | revised — <what changed and the new token delta>
```

If the issue already carries two recurrence comments, add yours and record with `work_note(kind: "observation", from: "<run ref>", …)` that this finding has now recurred three times.

### 3. No match, or a `COMPLETED` one — create the issue

Write the body to a file first — multi-line Markdown through `--body` is a quoting hazard on PowerShell.

```bash
gh issue create \
  --repo getsupraman/workman \
  --title "retro: <file>#<section-slug> — <symptom in ≤8 words>" \
  --label orchestrate-retro \
  --body-file .claude/retro/<run ref>/retro-issue.md
```

### 4. Record it

One `work_note(kind: "decision", from: "<run ref>", body: "Retro: filed <issue URL>")`, and one line in your closing report to the user. Do not summarize the issue back to them — they can open it.

### 5. No push access — write it locally instead

Write the body — same headings, same schema, nothing extra — to `.claude/retro/<run ref>/RETRO.md`. Post nothing. One `work_note(kind: "decision", from: "<run ref>", body: "Retro: no push access to getsupraman/workman — written to .claude/retro/<run ref>/RETRO.md")` and one line to the user naming the path and saying they can open an issue upstream if they think it generalises. Do not commit the file. Then stop.

---

## The issue schema

Exact: the retro **writes** it, the repo command **parses** it.

### Label

`orchestrate-retro` — every issue, no exceptions. It is the only thing the consumer filters on.

### Title

```
retro: <file>#<section-slug> — <symptom in ≤8 words>
```

`<file>` is the skill file relative to the skill root: `SKILL.md`, `references/briefs.md`, `references/decompose.md`, `references/retro.md`. `<section-slug>` is the target heading, lowercased and hyphenated.

### Body — these headings, in this order, all required

| Heading | Contents |
|---|---|
| `## Target` | Skill-root-relative path in backticks, then `›`, then the target section heading **verbatim**. One line. |
| `## Symptom` | What went wrong this run, in outcome terms. 1–3 sentences. What it cost, if you can name a number. |
| `## Root cause` | The sentence or omission in the target section that permitted it. Quote the offending text if it exists; say "no rule covers this" if it doesn't. |
| `## Proposed edit` | `Mode:` one of `replace` / `tighten` / `move` / `add`, then a fenced `text` block labelled `BEFORE:` (verbatim from the file, or `n/a` for mode `add`) and a second fenced `text` block labelled `AFTER:`. The consumer applies these mechanically — BEFORE must match the file character for character. |
| `## Token delta` | `+N` / `-N` / `±0` tokens, then one line on why this is the cheapest form of the fix. Net-additive proposals must justify why tightening existing text cannot achieve it. |
| `## Evidence` | Journal events, verify lines, report lines. Nothing from the worked repo. 5 lines max. |
| `## Run` | One line: `Repo: <name> · Slug: <run ref> · Date: <YYYY-MM-DD> · Reworks: <n> · Re-plans: <n> · Failed verifications: <n>` |

No other headings. No preamble above `## Target`. Nothing below `## Run`.

---

## Worked example

**Title:** `retro: references/briefs.md#recon-agents — recon claim nearly shipped unverified`

**Body:** (outer fence is four backticks only so the inner blocks survive; the issue body itself starts at `## Target`)

````markdown
## Target
`references/briefs.md` › **Recon agents**

## Symptom
A recon agent asserted that a `skillDirectories` setting exists in Claude Code. It does
not — extracting strings from the installed binary showed only `extraKnownMarketplaces`,
`enabledPlugins`, `skillOverrides`. The claim was about to be written into a user-facing
upgrade guide. Disproving it cost roughly 15k tokens that the run had not budgeted.

## Root cause
The section tells the orchestrator how to *scope* recon — one question, ≤15 lines,
paths not content — but nothing in it distinguishes a coordinate a recon agent read off
disk from a capability it asserted from memory. Both come back in the same ≤15 lines and
read with identical confidence, so the answer was treated as evidence.

## Proposed edit
Mode: add

```text
BEFORE:
- Ask for **≤15 lines with file paths**. Coordinates, not content.
```

```text
AFTER:
- Ask for **≤15 lines with file paths**. Coordinates, not content.
- **A recon agent asserting that a capability, setting, or flag exists is not evidence.** Before it reaches a user-facing doc, confirm it against `--help` or the binary, or drop the claim.
```

## Token delta
+27 tokens. Additive, but tightening cannot achieve it: the section has no sentence about
claim provenance to strengthen, and the same 27 tokens would have saved the ~15k spent
disproving one hallucinated setting.

## Evidence
Decision note, Phase 0 recon: the spec recon agent asserted `skillDirectories`;
binary string extraction disproved it before it reached `upgrade_guide.md`.

## Run
Repo: getsupraman/workman · Slug: one-plugin · Date: 2026-08-06 · Reworks: 0 · Re-plans: 1 · Failed verifications: 0
````
