# Contributing to Workman

Thanks for helping. This page says how to build and test each part, how a change gets to `main`, and
where issues and retros go. [AGENTS.md](AGENTS.md) is the same contract written for coding agents;
read it too if an agent does the work.

## The parts

| Path | What it is | Built with |
|---|---|---|
| `server/` | the Workman server (`Workman.slnx`) and its tests | .NET 10, Docker for the tests |
| `server/frontend/` | the dashboard | Node 24, pnpm 9 |
| `plugin/` | the `workman` Claude Code plugin: skills, the agents MCP server, the setup assets | Node 24, no dependencies |
| `.claude-plugin/marketplace.json` | the `supraman` marketplace | Claude Code |
| `docs/` | user guides and design documents, indexed by [docs/README.md](docs/README.md) | — |

## Build and test

Run what [CI](.github/workflows/ci.yml) runs for every part you touched. A pull request merges only
with CI green.

**Server.** The tests start Postgres 17 through Testcontainers, so Docker must be running.

```sh
dotnet build server/Workman.slnx
dotnet test server/Workman.Tests/Workman.Tests.csproj --no-build
```

`ImportVisibilityTests` pins the bytes of `tools/list`. A change to any tool's name, description or
schema repins its constants, and the pull request says so.

**Dashboard.**

```sh
cd server/frontend
pnpm install --frozen-lockfile
pnpm ts && pnpm build && pnpm verify
```

`pnpm verify:board` and `pnpm verify:links` need a running, populated server; see
[server/frontend/README.md](server/frontend/README.md).

**Plugin.** Only the `*.test.mjs` files are tests; `test/` also holds helpers.

```sh
cd plugin/skills/agents/server && node --test test/*.test.mjs
cd plugin/skills/setup/assets  && node --test test/*.test.mjs
claude plugin validate plugin
claude plugin validate .
```

`setup.sh` and `setup.ps1` are twins: a change to one is made to both.

## Pull requests

- **Pull requests only.** Nothing is pushed to `main` directly, and nothing is force-pushed. Branch,
  open a pull request against `main`, and fill in the template.
- **CI must be green** before a pull request merges.
- **A change that reaches installed users bumps `version`** in
  [`plugin/.claude-plugin/plugin.json`](plugin/.claude-plugin/plugin.json). Claude Code re-copies the
  plugin only when that version changes, so a skill or asset edit without a bump never arrives.
- **User-visible changes go in [CHANGELOG.md](CHANGELOG.md).**
- **Machine-facing names stay stable**: the MCP server name `workman`, the `work_*` tools, the
  `X-Workman-*` headers, the environment variable names and the settings keys. Renaming one
  needs a compatibility window.
- **Nothing private.** This repository is public. No client or company names, private hosts or
  domains, tokens, or live data, in code, docs, tests, fixtures or commit messages. Use `example.com`,
  `*.example`, `127.0.0.1` and synthetic data.

## The CLA

Contributions are accepted under a Contributor License Agreement. CLA Assistant asks you to sign it on
your first pull request, with a comment and a link; signing once covers every later pull request.
A Developer Certificate of Origin sign-off (`Signed-off-by`) is not needed.

## Issues and retros

- **Bugs and feature requests:** open an issue with the
  [bug report](.github/ISSUE_TEMPLATE/bug_report.md) or
  [feature request](.github/ISSUE_TEMPLATE/feature_request.md) template.
- **Retros from `/workman:orchestrate`:** the skill files a retrospective as an issue labelled
  `orchestrate-retro`, or writes it locally when it can't post. If you post one by hand, use the
  [orchestrate retro](.github/ISSUE_TEMPLATE/orchestrate_retro.md) template. The schema is in
  [plugin/skills/orchestrate/references/retro.md](plugin/skills/orchestrate/references/retro.md).
  Retros are public: strip client and product names, paths, private host names and code first.
- **Security vulnerabilities never go in a public issue.** Follow [SECURITY.md](SECURITY.md).

Everyone taking part follows the [Code of Conduct](CODE_OF_CONDUCT.md).
