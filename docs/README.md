# Workman docs

Workman keeps what your AI coding agents are doing outside their context window: every run, task,
rule, note and finding as its own record on one MCP server, with a dashboard for people. Start with
the [project README](../README.md) for what it fixes and how to install it; the pages below go
further.

## Getting started

| Doc | What it covers |
|---|---|
| [install.md](install.md) | Connecting to the hosted server, installing the plugin, self-hosting with `/workman:setup` (update, backup, restore), one server for a team, several public URLs with `WORKMAN_PUBLIC_URLS`, running from source |
| [concepts.md](concepts.md) | Projects, runs and items; refs and links; criteria and verification; claims; rules, assumptions and notes; roles; the loop; the dashboard |
| [../CONTRIBUTING.md](../CONTRIBUTING.md) | Building and testing the server, the dashboard and the plugin; pull requests and the CLA |

## Reference

| Doc | What it covers |
|---|---|
| [01-mcp-surface.md](01-mcp-surface.md) | **The tool reference.** Every `work_*` tool, the output contract (compact text, `structuredContent`, the attention channel), resources, roles and the tools each one sees |
| [05-server-instructions.md](05-server-instructions.md) | The exact text the server puts in every session's system prompt, block by block, with the reason for each |
| [06-rendering.md](06-rendering.md) | The exact output formats: the item row and its flags, `work_tree`, the `work_brief` recovery payload, the attention block, refusals |
| [07-guards.md](07-guards.md) | Forced closure: the transition table, the conditions for `done`, deferral as a move, claim leases, the `force` override |
| [10-read-api.md](10-read-api.md) | The JSON read API v1 the dashboard is built on: auth, the `ItemRef` shape, every endpoint |
| [11-sessions.md](11-sessions.md) | Sessions as records: how a session gets its name and role, and where the name shows |
| [13-dashboard.md](13-dashboard.md) | The dashboard as built: pages and URLs, readable `/r/` links, the command palette, the detail panel, the state vocabulary |
| [14-agent-launch.md](14-agent-launch.md) | `agent_launch` and friends: launching nested Claude Code sessions with a per-session credential |
| [../CHANGELOG.md](../CHANGELOG.md) | What changed in each server and plugin version, with migration notes |

## Concepts in depth

| Doc | What it covers |
|---|---|
| [02-data-model.md](02-data-model.md) | The Postgres model: one recursive `item` table and a dependency graph, documents, attempts, reports and verifications, assumptions, rules, notes, claims, the journal, tenants and row-level security |
| [04-self-explaining.md](04-self-explaining.md) | How the server teaches an agent to use it with no skill file, and the checks that prove it |
| [08-distribution.md](08-distribution.md) | Why the server runs on its own, how the plugin ships, every setting and credential, and serving agents and people from one process |
| [12-install-harness-identity.md](12-install-harness-identity.md) | What Claude Code's MCP support measurably does on a fresh machine, where configuration lives, and how sessions get an identity |
| [15-query-plans.md](15-query-plans.md) | The hot reads `EXPLAIN`ed against a seeded workspace: plans, times and the fixes |

## Design history

These record why Workman is built the way it is. They are dated and kept as written; where a later
decision changed one, the document says so in place.

| Doc | What it covers |
|---|---|
| [00-analysis.md](00-analysis.md) | What plan and ledger files actually hold, measured over the run artifacts the skills wrote, and why files are the wrong store |
| [03-stack-and-plan.md](03-stack-and-plan.md) | The stack (.NET 10, the MCP C# SDK, Postgres 17, SolidJS) and the build plan it was delivered against |
| [ACCEPTANCE.md](ACCEPTANCE.md) | The cold-start acceptance runbook and its results: can an agent use the server untaught? |
| [PROBE.md](PROBE.md) | What Claude Code measurably surfaces of an MCP server: the evidence several design decisions rest on |
| [repo-keys.json](repo-keys.json) | The repository identity rule as a table, run by the server's and the plugin's tests |

## The case, in one paragraph

State that is a side effect of narration is never maintained; state that is the primary write is.
Plan and ledger files mix a dozen kinds of knowledge (tasks, decisions, rules, corrections, findings)
in prose an agent has to re-read and re-parse, cannot retract or address, and that nothing checks.
Workman makes each of those a record with a state, so a unique constraint and a foreign key do what a
plan audit used to spend tokens on, a guard decides when work is done, and a session gets back only
what binds it. Move the state, keep the judgment.
