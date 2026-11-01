# Workman

A self-hosted work tracker for coding agents: an MCP server with a Postgres store and an operator
dashboard, plus the Claude Code skills that drive it.

- `server/` — the server, the dashboard and their tests
- `plugin/` — the Claude Code plugin and its skills
- `docs/` — the design documents ([docs/README.md](docs/README.md))

## Install the plugin

In Claude Code:

```
/plugin marketplace add getsupraman/workman
/plugin install workman@supraman
```

The skills are then listed as `/workman:<skill>`. `/workman:setup` gets the WorkState server running
and registered; the other skills need it.

## Coming from rlarch

These skills used to ship in the `rlarch` plugin. They now live here, under new names:

| rlarch | workman |
|---|---|
| `/rlarch:workstate` | `/workman:plan` |
| `/rlarch:workstate-setup` | `/workman:setup` |
| `/rlarch:workstate-import` | `/workman:import` |
| `/rlarch:controller` | `/workman:controller` |
| `/rlarch:orchestrate` | `/workman:orchestrate` |
| `/rlarch:nested-claude` | `/workman:agents` (with the agents MCP server, `mcp__plugin_workman_agents__*`) |

`code-comments`, `dotnet-reflect`, `dotnet-source` and `ts-source` stay in rlarch. The WorkState
server registration (`workstate`, the `work_*` tools) is unchanged.

Licensed under the Apache License 2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE).
