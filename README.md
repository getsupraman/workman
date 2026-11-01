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

Licensed under the Apache License 2.0; see [LICENSE](LICENSE) and [NOTICE](NOTICE).
