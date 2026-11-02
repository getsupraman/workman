# AGENTS.md — the repo contract

Workman is a public repository: the Workman server and the `workman` Claude Code plugin that drives
it. Read this before you change anything.

## Layout

| Path | What it is |
|---|---|
| `.claude-plugin/marketplace.json` | the `supraman` marketplace; lists one plugin, `workman`, at `./plugin` |
| `plugin/` | the `workman` plugin: `.claude-plugin/plugin.json`, `.mcp.json` (the agents MCP server) and `skills/` |
| `plugin/skills/{plan,orchestrate,controller,agents,setup,import}/` | the six skills, invoked as `/workman:<skill>` |
| `plugin/skills/agents/server/` | the agents MCP server (`agent_launch`, `agent_list`, …), plain Node ES modules, no dependencies |
| `plugin/skills/setup/assets/` | `setup.sh` / `setup.ps1` (twins: change both), `compose.yml`, `session-header.mjs`, `find-shadows.mjs` |
| `server/` | the Workman server: `Workman.slnx` (.NET 10), `frontend/` (SolidJS dashboard), `Dockerfile`, composes |
| `docs/` | the design documents, indexed by `docs/README.md` |
| `.github/workflows/` | `ci.yml` (build and test), `image.yml` (publishes the server image) |

## Build and test

Run what `.github/workflows/ci.yml` runs for every part you touched:

```sh
# .NET (Testcontainers needs a running Docker)
dotnet build server/Workman.slnx
dotnet test server/Workman.Tests/Workman.Tests.csproj --no-build

# dashboard
cd server/frontend && pnpm install --frozen-lockfile && pnpm ts && pnpm build && pnpm verify

# plugin node tests (only *.test.mjs: test/ also holds helpers)
cd plugin/skills/agents/server && node --test test/*.test.mjs
cd plugin/skills/setup/assets  && node --test test/*.test.mjs

# manifests
claude plugin validate plugin
claude plugin validate .
```

`ImportVisibilityTests` pins `tools/list` (tools sorted by name, `\r\n` normalised to `\n`). A change
that alters any tool's name, description or schema must repin its constants and say so in the PR.

## Rules

- **A change that reaches installed users bumps `plugin/.claude-plugin/plugin.json` `version`.**
  Claude Code re-copies the plugin only when that version changes, so a skill or asset edit without a
  bump never arrives.
- **PR-only to `main`.** No direct pushes, no force-pushes.
- **Nothing private (C95).** No client names, private hosts or domains, ssh details, tokens, or live
  data — in code, docs, tests, fixtures or commit messages. Use placeholders (`example.com`,
  `127.0.0.1`) and synthetic data.
- Machine-facing names stay stable: the MCP server name `workman`, the `work_*` tools, the
  `X-Workman-*` headers, the env var names and the `WorkState:` settings keys. Renaming any of them
  needs a compatibility window, as the move from `workstate` / `X-WorkState-*` has (`CHANGELOG.md`:
  the old names work until server 2.0 / plugin 2.0; `WorkmanHeaders` is the one place that reads them).
