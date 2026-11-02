<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".github/assets/hero-dark.svg">
    <img src=".github/assets/hero-light.svg" alt="Workman: run your crew of AI coding agents like a shift. A terminal shows work_status refusing to close a task whose done-criteria are unchecked and that no orchestrator verified." width="100%">
  </picture>
</p>

<p align="center">
  <a href="https://github.com/getsupraman/workman/actions/workflows/ci.yml"><img src="https://github.com/getsupraman/workman/actions/workflows/ci.yml/badge.svg" alt="CI"></a>
  <a href="LICENSE"><img src="https://img.shields.io/github/license/getsupraman/workman" alt="License: Apache-2.0"></a>
  <a href="https://github.com/getsupraman/workman/releases"><img src="https://img.shields.io/github/v/release/getsupraman/workman" alt="Release"></a>
  <a href="https://github.com/getsupraman/workman/pkgs/container/workman"><img src="https://img.shields.io/badge/image-ghcr.io%2Fgetsupraman%2Fworkman-1f2430" alt="Image: ghcr.io/getsupraman/workman"></a>
</p>

<p align="center">
  <a href="#install"><b>Install</b></a> ·
  <a href="#what-you-get"><b>What you get</b></a> ·
  <a href="#why-not-just-keep-a-planmd"><b>Why</b></a> ·
  <a href="#self-hosting"><b>Self-host</b></a> ·
  <a href="docs/README.md"><b>Docs</b></a>
</p>

**Workman** is the record of what your AI coding agents did. Every plan, task, claim and test run goes
into one place over MCP, so *claimed* and *proven* stay apart: a task closes only when its criteria are
checked and someone other than its author ran the verification. One record covers all your machines
and your whole team.

## Install

**Hosted, on [work.supraman.ai](https://work.supraman.ai)** (access by invitation today). Connect
Claude Code to it once:

```bash
claude mcp add --transport http --scope user workman https://work.supraman.ai/mcp
```

Claude Code opens a browser sign-in on first use, so you never paste a token. Then install the
skills, in Claude Code:

```
/plugin marketplace add getsupraman/workman
/plugin install workman@supraman
```

Restart Claude Code and you're done: the `mcp__workman__*` tools and the `/workman:*` skills are there.

<details>
<summary>Run it yourself instead</summary>

Install the plugin as above, then run `/workman:setup`. It starts the server and its Postgres locally
in Docker from `ghcr.io/getsupraman/workman`, registers it with Claude Code under `workman` with
browser sign-in, and backs it up on request. A machine needs Docker and nothing else.
[Self-hosting](#self-hosting) has the details.

</details>

## What you get

<table>
<tr>
<td width="50%" valign="top">

🗺️ **[`/workman:plan`](plugin/skills/plan/SKILL.md)**

**Turn a request into a plan you can approve.** A run with a goal, phases, done-criteria and a
verify command per task, binding rules and open assumptions. Then it stops and waits for you.

💬 *"Plan the saved-carts feature."*<br>
💬 *"What should we build next?"*

⚙️ Needs a Workman server

</td>
<td width="50%" valign="top">

🎛️ **[`/workman:controller`](plugin/skills/controller/SKILL.md)**

**Work through a whole backlog, one agent per unit.** It briefs a nested Claude for each unit,
steers it, verifies what came back itself, lands it and records the decision.

💬 *"Run these five issues as a controller."*<br>
💬 *"Approved, execute as `/workman:controller`."*

⚙️ Needs a Workman server

</td>
</tr>
<tr>
<td width="50%" valign="top">

🧭 **[`/workman:orchestrate`](plugin/skills/orchestrate/SKILL.md)**

**Finish a plan too big for one context window.** It delegates every task to subagents, checks each
result against the goal, and re-plans when reality diverges.

💬 *"Execute this migration plan."*<br>
💬 *"Implement this spec end to end."*

⚙️ Needs a Workman server

</td>
<td width="50%" valign="top">

🤖 **[`/workman:agents`](plugin/skills/agents/SKILL.md)**

**Run Claude Code inside Claude Code.** Launch named background sessions, each with a fresh
context, talk to them over READY / BLOCKED / DONE, and get woken when one is stuck.

💬 *"Launch a worker for the cart refactor."*<br>
💬 *"Which agents are still running?"*

⚙️ Needs Node (it ships its own MCP server)

</td>
</tr>
<tr>
<td width="50%" valign="top">

🛠️ **[`/workman:setup`](plugin/skills/setup/SKILL.md)**

**Get a server running and connected.** It finds what's missing on this machine and does only that:
pull the image, start it, register it, prove it answers. It also backs up and restores.

💬 *"Set up Workman locally."*<br>
💬 *"Back up my Workman database."*

⚙️ Needs Docker

</td>
<td width="50%" valign="top">

📥 **[`/workman:import`](plugin/skills/import/SKILL.md)**

**Bring an existing repo's plans in, minus the stale parts.** It reads PLAN.md files, TODOs and ADRs,
checks each against the current code, and imports only what is still true, with its source.

💬 *"Import this repo's plans into Workman."*

⚙️ Needs a session with the import grant

</td>
</tr>
<tr>
<td width="50%" valign="top">

📊 **[The dashboard](docs/13-dashboard.md)**

**See every run, and what needs you, at a glance.** What each agent reported sits next to what was
verified, with the inbox, live activity and sessions alongside.

💬 *"What's blocked right now?"*<br>
💬 *"Did anyone actually run the tests on 1.2?"*

⚙️ Needs nothing: it's on the same server

</td>
<td width="50%" valign="top">

🔌 **[The `work_*` tools](docs/01-mcp-surface.md)**

**One MCP server every session talks to.** `work_brief` hands a fresh session its goal and its next
step; `work_status` refuses to close what isn't proven, and says what's missing.

💬 *"Where are we?"*<br>
💬 *"Close 1.2."*

⚙️ Needs nothing: the skills call them for you

</td>
</tr>
</table>

## Why not just keep a PLAN.md?

A plan file, a todo list or the agent's own word all have the same weakness: nothing checks them.
An agent writes "all tests pass" and ticks the box, and the next session has no way to tell a claim
from a result. Across many sessions and machines, those files drift apart and go stale.

<p align="center">
  <picture>
    <source media="(prefers-color-scheme: dark)" srcset=".github/assets/stats-dark.svg">
    <img src=".github/assets/stats-light.svg" alt="20 work_* tools on one server; 5 conditions guard done; a worker's token sees 6 tools; 935 tests passing" width="100%">
  </picture>
</p>

- **Claimed and proven are kept apart.** A worker's report is recorded as a claim. A task closes only
  when its criteria are checked and an orchestrator ran its verification. The refusal names exactly
  what's missing.
- **Nothing gets dropped silently.** Deferring is a move: it creates a real open item somewhere else.
  A forced close is allowed, but `work_audit` lists it permanently.
- **One record for every machine and every co-worker.** Your laptop, your desktop and a teammate's
  sessions all write to the same server. One dashboard shows the whole team's runs.
- **Hand-over without the archaeology.** A new session calls `work_brief` and gets the goal, where
  things stand and its next step. Notes addressed to a task wait for whoever picks it up.
- **Roles are enforced by the server.** A worker's token sees six tools: it can report and verify,
  but it can't close, create or re-plan.

### Coming

Self-service sign-up, inviting your team, and a free plan alongside paid plans.

## Screenshots

From a local server with demo data.

**Overview:** what needs you, who is working on what, and every run's progress.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/assets/dashboard-overview-dark.jpg">
  <img src=".github/assets/dashboard-overview-light.jpg" alt="The Workman dashboard overview: two items need you, four are being worked on, and the runs of two projects with their progress bars." width="100%">
</picture>

**A run:** its phases and tasks, with a blocker finding under the task it blocks.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/assets/dashboard-run-dark.jpg">
  <img src=".github/assets/dashboard-run-light.jpg" alt="A run page: the checkout-v2 run's three phases and six tasks, one done and two in progress, and its goal in the side panel." width="100%">
</picture>

**A task's evidence:** what the agent reported, next to what was verified and by whom.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset=".github/assets/dashboard-item-dark.jpg">
  <img src=".github/assets/dashboard-item-light.jpg" alt="Task 1.2's evidence tab: the worker reported done, and the only verification was run by that same worker." width="100%">
</picture>

---

## How it works

The server gives every session the same loop, in its MCP instructions:

```
work_brief → work_next → work_status(in_progress) → do the work
           → work_verify → work_status(done) → work_next
```

- **`work_brief`** is the recovery payload: the goal, where the run stands, the literal next call,
  the rules that bind you, open assumptions and notes addressed to you. It's generated, so it can't go
  stale.
- **`work_status(ref, "done")`** is guarded. It is refused unless every done-criterion is checked, an
  orchestrator ran a passing verification, no child is still open and no blocker finding targets it.
  It lists every unmet condition at once, with the call that fixes each. The full rules are in
  [docs/07-guards.md](docs/07-guards.md).
- **Claims are leased.** Starting work takes a claim with a lease, so two sessions don't silently work
  the same task. When a lease lapses, the task goes back to `ready` for someone else.
- **Every result ends with an `attention` block**: expired claims, assumptions past their revisit
  point, notes for the item you're about to brief.

Roles come from the token or the sign-in: workers report and verify; orchestrators and controllers
plan, judge, close and commit.

## Self-hosting

`/workman:setup` is the supported path. It writes a local `.env` with random tokens, pulls
`ghcr.io/getsupraman/workman` (or builds from a clone), starts the server and Postgres with Docker
Compose, and registers `http://127.0.0.1:5290/mcp` as `workman`. The dashboard is on the same port.
The model never reads or pastes a token. To run the server from source instead, see
[AGENTS.md](AGENTS.md#build-and-test) and [server/frontend/README.md](server/frontend/README.md).

## Docs

| | |
|---|---|
| [docs/README.md](docs/README.md) | The design, and an index of every document |
| [docs/01-mcp-surface.md](docs/01-mcp-surface.md) | The MCP tools, their output contract and the attention channel |
| [docs/07-guards.md](docs/07-guards.md) | Forced closure: the transition table and the conditions for `done` |
| [docs/10-read-api.md](docs/10-read-api.md) | The JSON read API the dashboard is built on |
| [docs/13-dashboard.md](docs/13-dashboard.md) | The dashboard: pages, the detail panel, the state vocabulary |
| [CHANGELOG.md](CHANGELOG.md) | What changed in each server and plugin version |

## Repository layout

```
.claude-plugin/marketplace.json   # the "supraman" marketplace: one plugin, workman
plugin/                           # the workman plugin: plugin.json, .mcp.json, skills/
  skills/{plan,orchestrate,controller,agents,setup,import}/
server/                           # the server (.NET 10), the dashboard (frontend/), Dockerfile
docs/                             # the design documents
.github/                          # CI, the image workflow, and the images on this page
```

## Contributing

Read [AGENTS.md](AGENTS.md) first. Changes go to `main` by pull request, and a change that reaches
installed users bumps `version` in `plugin/.claude-plugin/plugin.json`.

## License

[Apache License 2.0](LICENSE); see also [NOTICE](NOTICE).
