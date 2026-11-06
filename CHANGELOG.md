# Changelog

All notable changes to the Workman server and the `workman` plugin are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). The server's version is `serverInfo.version`; the plugin's is
`plugin/.claude-plugin/plugin.json`.

## [Unreleased]

### Dashboard

- **Needs you has a filter bar** like a run page's: *All*, *Questions*, *Blocked*, each with its count
  (`?kind=`); the filter stays when a row is opened beside the list, and ↓/↑ walk only the rows it shows.

## [3.0.2] — 2026-11-06

Server 3.0.2 (server and dashboard). No migration; the plugin stays 3.0.1.

### Server

- **`POST /api/agent-sessions` takes `takeoverFrom`:** with `replace: true`, a launcher renamed under the same
  user (`foreman/x` → `supraman/x`) takes its own row when `takeoverFrom` equals the row's `launchedBy`; the
  row keeps its id, claims and history, and `session_launcher_taken_over` records `from` and `to`. Every other
  launcher mismatch is still `409`.

### Dashboard

- **Keyboard:** ↓/↑ (and Home/End) walk a run's plan and show each item in the detail panel; the same keys
  walk Needs you, Backlog, a project's runs and Activity. Alt+↓/↑ (⌥ on macOS) open the next/previous run
  in sidebar order. `?` and the palette's *Keyboard shortcuts* list every key (doc 13, Keyboard). Keys never
  act in a text field or while a dialog is open.
- **Sidebar tip:** a run or project shows its whole title and its full ref beside the link on hover or focus.
- **Needs you is a decision list:** each question or blocked item is a row with its title first, the kind
  and ref muted below and the one line of why; it opens beside the list (`/inbox/{item}`), and an item
  answered meanwhile stays struck through until you move on. Housekeeping is one collapsed line per kind
  and run; unread notes are no longer shown. The overview's top five use the same row.

## [3.0.1] — 2026-11-06

Server 3.0.1 and plugin 3.0.1. No migration.

### Server

- **A rotated refresh token still redeems for 10 minutes** (was OpenIddict's 30 seconds), so a client that
  refreshes late or twice no longer has every token of its authorization revoked.
- **A refresh token reused past that window is logged** at Warning under `Workman.Auth.OAuth.RefreshReuse`,
  with the client id and the authorization id, never the token.
- **`GET /api/v1/inbox` rows carry structured fields** beside `what` and `call`: `audience`, `detail`,
  `actor`, `tree`, `target`, `count` and `url` (doc 10); the dashboard's Needs you page reads them.

### Plugin

- **Minted tokens and launch records move to `~/.workman/agents/`** (directory `0700`, files `0600`):
  `mcp-<name>.json` and `<name>.json`. Nothing secret is written into the repo any more, so the
  `.claude/briefs/` gitignore check is gone; `agent_stop` and a failed launch delete the config. The old
  `.claude/briefs/mcp-<name>.json` and `os.tmpdir()`/`CLAUDE_PLUGIN_DATA` locations are not read (no
  migration). A name that is still recorded is refused until `agent_stop`; names may not start with `mcp-`.
- **`agent_launch` refuses a shadowed `repo_root`**: a local-scope (`~/.claude.json`) or project-scope
  (`.mcp.json`) `workman` entry there would replace the worker's minted one. The check is
  `find-shadows.mjs`'s, shared with setup.
- **`server.json` is read as `new URL(url).origin`**: a trailing slash, an upper-case host or a default port
  are accepted; a path, query or credentials, or an `mcpName` other than `workman`, are refused.
- **Never-probe where a stuck agent looks**: the agents server instructions, the `agent_launch` description
  and the server.json, sign-in, mint and launch refusals carry it, each naming its next step
  (`/workman:setup`, `agent_signin`, or the human runs `/mcp` → workman). The worker header says: a failing
  `work_*` call or only authenticate tools also mean `BLOCKED no Workman tools`; never authenticate, never
  `claude mcp`. `workman.role` `"none"` says it runs with your own MCP servers and identity.
- **Auth-cache clear** edits the cache in the `CLAUDE_CONFIG_DIR` the worker is spawned with, and re-reads it
  just before writing.
- **setup.sh**: a kept registration that points elsewhere now writes `server.json` for that registration's
  origin (or says why not), so `server.json` matches the registration in use; `next` says `already registered`
  for a kept one; the shadow check also reads the `.mcp.json` of every project `~/.claude.json` lists.
- **session-header.mjs** kills its lookups' whole process tree on its time budget (`taskkill /T /F` on
  Windows, the process group elsewhere).

## [3.0.0] — 2026-11-06

Server 3.0.0 and plugin 3.0.0. No migration.

### Plugin

- **Setup is OAuth only.** `/workman:setup` registers `workman` with OAuth sign-in and writes
  `~/.workman/server.json` (`{"url", "mcpName"}`), which every plugin tool reads; there is no default server.
  `--auth` and `--role` are gone, and so is `setup.ps1` (`setup.sh` runs under Git Bash on Windows). The
  install's `.env` holds the dashboard and Postgres passwords only. `--dry-run` reads the existing registration
  from Claude Code's config file instead of `claude mcp get/list`.
- **agents server.** `agent_launch` takes the Workman server from `server.json` only: `workman.env_file` and
  `workman.url` are removed, and `agent_signin`/`agent_signout` take no parameters. Before a minted launch it
  deletes the stale `workman` entry from Claude Code's `mcp-needs-auth-cache.json`, and the protocol header
  tells a worker without `work_report` to send `BLOCKED no Workman tools`. The fallback launch scripts
  (`Start-NestedAgent.ps1`, `start-nested-agent.sh` and their `Stop-` twins) are deleted.
- **Never probe.** The agents, controller and setup skills tell agents never to run `claude mcp get`,
  `claude mcp list` or a `claude` process to check Workman: a token-less request leaves a needs-auth entry
  that makes Claude Code skip every server named `workman` for 15 minutes, launched workers included.
- **Skills rewritten** for size: legacy text removed, restatements of the server instructions and of
  `/workman:agents` cut from the other skills.

### Removed

- The static role tokens and the reader token. `Workman:Auth:WorkerToken`, `OrchestratorToken`, `ControllerToken`
  and `ReaderToken` (`WORKMAN_WORKER_TOKEN`, `WORKMAN_ORCHESTRATOR_TOKEN`, `WORKMAN_CONTROLLER_TOKEN`,
  `WORKMAN_READER_TOKEN`) are no longer read, and `Workman:Workspace`, their workspace binding, goes with them.
  People sign in with OAuth; agents use the per-session `wss_` tokens a signed-in session mints; the dashboard
  keeps its cookie. A former static token is answered 401. The bearer scheme is now `WorkmanSessionToken`.
- A worker or orchestrator session named by `X-Workman-Agent` or `X-Workman-Client`: only an OAuth caller names
  its session by header (or `work_session`) now; a minted token's session is its credential's.

### Changed

- Every request that ends 401 logs one Information line (`Workman.Auth.Challenge`): method, path,
  `X-Workman-Client`, `X-Workman-Agent`, `User-Agent`, whether an `Authorization` header was present, and why
  each bearer scheme refused it. Never the `Authorization` header's value.
- The server instructions tell agents never to run `claude mcp get/list` or start a claude process to check
  Workman.

## [2.4.2] — 2026-11-06

Server 2.4.2. The plugin stays at 2.3.3. No migration.

### Added

- The dashboard's Brief tab shows the rules the brief clipped. Its `+n more — read work://workspace/conventions`
  line is a link that opens every rule active in the item's scope below the brief, from the new
  `GET /api/v1/items/{id}/conventions` (doc 10), the same text the MCP resource returns for that item.

## [2.4.1] — 2026-11-05

Server 2.4.1. The plugin stays at 2.3.3. One migration, `ClaimWorkspacePolicy`; back up before upgrading.

### Changed

- `claim`'s row-level policy compares the claim's own `key_workspace_id` with the tenant instead of reading its
  item. The column is backfilled from the item and made `NOT NULL`; a write must still name an item of the
  tenant. The project-access check the item lookup carried is now claim's own `project_access` policy, the one
  `verification` has. The expired-claims attention read no longer builds a hashed subplan over every item of
  the workspace: 2.71 → 0.66 ms per run and 2.76 → 1.24 ms per project on the 5k seed (doc 15 §7.2).
- `work_constrain` explains what a rule without `scope` does: it binds every project in the workspace, and the
  other projects read it as their own. The `scope` parameter keeps an unscoped rule to one that holds in any
  repo or language, and a rule recorded without a scope says so in its result: if it names a repo, a language,
  a stack or a host, retire it and record it again with the project as its scope.


## [2.4.0] — 2026-11-04

Server 2.4.0. The plugin stays at 2.3.3. No migration.

### Added

- `work_update` moves a repo between projects or removes it from one, with no database edit.
  `work_update(ref:"<project>", repo:{remote, root}, take:true)` moves those keys from the project that holds
  them, in one transaction and for every user: each user's root keeps its user, so their `work_brief(repo:…)`
  opens the new project too. `work_update(ref:"<project>", unbind:{remote, root})` removes the remote and the
  root for every user it is bound for in that project; a project left with no key stays a valid, unbound
  project. Both need an orchestrator and membership of every project involved: rows of a project the caller is
  not a member of stay where they are, and the bind is refused as before. Each move or unbind journals a `repo`
  event on every project it touches. The "already bound to project X" refusal now names the `take:true` call.

### Fixed

- `work_get(include:"deps")` link lines print the other end of a link as a ref that resolves to it alone, as
  the `←` flag on the same item does. A link survives `work_move`, and an item moved to another project printed
  by its bare ref, which read as an item of the current run.

## [Plugin 2.3.3] — 2026-11-04

- Changed: `/workman:import` writes self-contained records. A record's title, intent, criteria and body
  inline everything it needs, and `source` is provenance only, so the planning files may be deleted after
  the import (by the human, never by the import) without losing anything. References are kept only to
  living contracts (`CLAUDE.md`, `AGENTS.md`, rule files, kept specs and ADRs) and to code. A re-import
  rewrites records that still point into a planning file, and the final check asks of every record
  whether it would survive its source file being deleted.

## [Plugin 2.3.2] — 2026-11-04

- Changed: `/workman:import` in a session without the import grant no longer stops with a one-line
  refusal. It says in plain words why that session cannot write an import, and offers to start one that
  can (`agent_launch` as orchestrator with `grants: ["import"]`, the repo as cwd, the human's arguments
  as its prompt), running `agent_signin` first when it is not signed in.

## [2.3.2] — 2026-11-04

Server 2.3.2. The plugin stays at 2.3.1. No migration.

### Changed

- The dashboard reads the run boards in one `GET /api/v1/boards` call instead of one `/api/v1/runs/{id}/board`
  per run, so a page opens without waiting on a board per run and the sidebar bars arrive together. The new
  endpoint returns every unarchived run's board the caller can see, in the overview's order, or with
  `runs=<id>,<id>,…` (at most 200) just those; each board is the one `/runs/{id}/board` returns, read from one
  snapshot. A live frame re-reads the boards of the runs it names in the same single call.

## [2.3.1] — 2026-11-03

Server 2.3.1 and plugin 2.3.1. No migration.

### Fixed

- A blocker in another run or project prints as a ref that resolves to it alone. Links are workspace-wide, but
  the `work_get` and `work_tree` blocked-by flag (`← 3`), the `work_threads(include:"deps")` lines and the
  `work_audit` dep rows printed the blocker by its bare ref, which could name a different item or several. A
  blocker in the waiting item's run still prints bare; one anywhere else prints the shortest ref that resolves
  to it alone, such as `other-project/r1/3`, and the call hints on those lines use the same string.

## [Plugin 2.3.1] — 2026-11-03

- Changed: the agents skill names `agent_launch` without the `mcp__plugin_workman_agents__` prefix. A session
  that adds the plugin's `agents` server to its own MCP config (as `--strict-mcp-config` sessions must) lists
  it under another prefix, such as `mcp__agents__agent_launch`.

## [2.3.0] — 2026-11-03

Server 2.3.0 and plugin 2.3.0. No migration.

### Added

- A task close names the findings it leaves open. When `work_status(ref, "done")` succeeds on an item with
  open non-blocker findings directly under it, the result lists up to three as `ref  title`, counts the rest,
  and gives the two calls that resolve them (`to:"done", commit:…` and `to:"deferred", target:"backlog"`).
  The close still goes through. No migration.

- Open findings are shown as what they are. Findings are still never part of an "X of Y", but a run page could
  read "8 of 10 done" over dozens of open findings listed flat among its tasks. The run page now lists the
  findings filed directly on the run in their own "Findings" group after the tasks and phases (a task's or
  phase's own findings stay under their owner), and its header says "N findings open" beside "X of Y done".
  `work_tree` prints the same count after a container's rollup (`8/11 done · 3 findings open`), and the
  `/api/v1` rollup carries `findingsOpen`: the unclosed findings anywhere below the item. The header reads that
  field. No migration.

### Changed

- A run or phase no longer closes over open findings. Every open finding anywhere in its subtree, at any
  severity and depth, is now an unmet condition of `work_status(run|phase, "done")`, listed up to three with a
  count of the rest, each with its resolving calls (done with a commit, cancelled with a reason, or deferred
  with a target, which moves it out of the run). Tasks and sub-tasks are unchanged: a minor finding still does
  not hold a task close. No migration.

- The dashboard's prompts say who owns a finding. `finalize-run` resolves or moves every open finding in the run
  (done with the commit that fixed it, deferred with a target and the reason, or moved under the unit that will
  fix it); `audit` ("Did we cheat?") looks for a finding still open under an item marked done; `finalize` and
  `cleanup` give every finding left open an owner. A run does not close while a finding under it is open.

### Plugin 2.3.0

- Changed: the controller, plan and orchestrate skills and `docs/language.md` teach the same practice. A finding
  belongs to the item it sits under: when an audit or a review becomes units, each finding moves under the unit
  that will fix it (`work_move`), and when that unit closes it resolves each finding under it - done with the
  unit's commit, or deferred with a target and the reason.

## [2.2.0] — 2026-11-03

Server 2.2.0 and plugin 2.2.0. No migration.

### Fixed

- One run, one "X of Y". `work_tree` counted every descendant (phases with children included) and `work_brief`
  counted tasks and sub-tasks only, while the dashboard and `/api/v1` count leaves plus childless phases and
  features. All of them now use that one definition (`WorkmanQueries.Counted`), so the same run, phase or
  project shows the same done/total everywhere. The run, gate and project briefs say "N of M done" instead of
  "N of M tasks done", since the count is no longer only tasks. No migration.

### Plugin 2.2.0

- Added: `/workman:setup --url <origin>` (`setup.ps1 -Url`) registers a **remote** Workman server, such as the
  hosted `https://work.supraman.ai`, as well as the local Docker one. It touches no Docker and writes no `.env`;
  it copies the session helper to `~/.workman/`, proves the server asks for sign-in (`POST /mcp` without a token
  answers `401` with a `resource_metadata=` link that answers `200`) before it registers anything, then registers
  `<origin>/mcp` with OAuth and the helper and runs the shadow check. An existing registration that points
  elsewhere is kept unless `--force`, which re-points it. `--url` refuses the local-install flags and tokens
  (`--clone`, `--port`, `--project`, `--image-version`, `--update`, `--backup`, `--restore`, `--auth token`,
  `--role`). The README and [docs/install.md](docs/install.md) now connect the hosted server this way.
- Fixed: `setup.ps1` wrote the session helper's `headersHelper` with a trailing backslash.


## [2.1.0] — 2026-11-03

Server 2.1.0 and plugin 2.1.1: the 1.x rename workarounds go, and rules number correctly again. One additive migration (`ConstraintRefAllocator`).

### Changed — the dashboard's prompts state the goal

- The copy-paste prompts (`PromptCatalogue`, doc 13) no longer spell out `work_*` calls. Each states its goal
  in the words of the new [docs/language.md](docs/language.md), starts from the brief of the scope it was
  copied from, and still proposes first and waits for a yes before it closes, cancels, defers, merges, moves
  or archives.
- New prompts: *Plan a feature*, *Clean up* (replaces *Housekeeping*; its count is the project's open items),
  and *Step back* on a project; *Finalize run*, *Step back* and *What's blocking* on a run; *Still needed?* on
  an item. *Status report* reads *Where are we*, and the run's *Audit* reads *Did we cheat?*.
- *Review rules* on a project and a run, shown while a binding rule is active in scope: realign the rules with
  the current code and the human's later decisions, propose keep, reword, retire or rescope and the missing
  rules, and never retire a red line without an explicit yes for it.
- `docs/language.md`: the vocabulary for talking to an AI that uses Workman, with example conversations.

### Fixed

- `work_constrain` numbers a new rule past the rules of projects the caller is not a member of. Its probe read
  `constraint_rule` under project access, so it offered a `C…` a hidden rule held and every new rule was refused by
  `constraint_ref_unique`. The number now comes from `workman_free_constraint_ref` (migration
  `ConstraintRefAllocator`), which counts every rule of the workspace and returns only the number.

### Removed

- Server: the OAuth scope aliases `workstate.read` and `workstate.sessions`. An authorize, token or refresh
  request naming one is refused with `invalid_scope`; a token granted under one before the upgrade no longer
  reaches `/api` (`403`), so its client signs in again for `workman.read` / `workman.sessions`. A client row
  registered under 1.x keeps its inert `scp:workstate.*` permissions beside the 2.0 ones.
- Dashboard: the one-time move of a remembered choice from its `workstate.*` localStorage key to `workman.*`.
  A choice still stored under the old key is forgotten once: the default applies.
- Plugin (`/workman:setup`): the move of a 1.x `workstate` registration to `workman` (`move-registration.mjs`).
  Setup now registers `workman` beside it; remove the old one with `claude mcp remove workstate -s <scope>`.

### Plugin 2.1.1

- Removed: `agent_launch` no longer accepts `workstate: {…}`, the 1.x name of `workman: {…}`; it is refused as an
  unknown parameter.

## [Plugin 2.0.1] — 2026-11-02

- `agent_launch` accepts `workstate: {…}` as the 1.x name of `workman: {…}` (one or the other, not both), so a
  Supraman 0.1.2 repo-controller can still launch units. It goes when Supraman has switched.

## [2.0.0] — 2026-11-02

Server 2.0.0 and plugin 2.0.0 ship together: the rename to Workman is complete. Also in this release:

- The .NET projects, namespaces and types are `Workman.*` (`server/Workman.slnx`; the image runs `Workman.Api.dll`).
- The skills, the agents MCP, the setup scripts and the docs say Workman in their prose and messages.

### Server 2.0.0 — every machine-facing and stored name says workman

The compatibility window 1.2.0 opened closes here: the old names below are **no longer read**, except the
two OAuth scopes.

| What | 1.x | 2.0 |
|---|---|---|
| settings section | `WorkState:*` (`WorkState__*`) | `Workman:*` (`Workman__*`) |
| connection string | `ConnectionStrings:WorkState` | `ConnectionStrings:Workman` |
| environment | `WORKSTATE_<X>` (`_PUBLIC_URL`, `_TRUSTED_PROXIES`, `_HEALTHCHECK_URL`, the compose ones…) | `WORKMAN_<X>`, same suffix; `WORKMAN_PUBLIC_URLS` unchanged |
| compose defaults (`server/compose.yml`, `compose.dev.yml`) | project `workstate`, volume `workstate-db-data`, `POSTGRES_DB`/`POSTGRES_USER` `workstate` | `workman`, `workman-db-data`, `workman` (dev: `workman-dev`, `workman-dev-db-data`) |
| request headers | `X-WorkState-*` read beside `X-Workman-*` | `X-Workman-*` only |
| response header on `/api/v1/*`, `/api/stream` | `X-WorkState-Api` and `X-Workman-Api` | `X-Workman-Api` only |
| dashboard cookie | `workstate.dashboard` | `workman.dashboard` |
| auth schemes, claim types | `WorkState*`, `workstate:*`, `workstate_*` | `Workman*`, `workman:*`, `workman_*` |
| data protection | application `workstate`, key-ring purpose `WorkState.OAuth.Keys` | `workman`, `Workman.OAuth.Keys` |
| OAuth scopes for `/api` | `workstate.read`, `workstate.sessions` | `workman.read`, `workman.sessions` advertised; **the old two stay accepted** in authorize, token and refresh requests, granting the same access, until Supraman has switched |
| consent form field | `workstate_tenant` | `workman_tenant` |
| MCP resources | `workstate_*`, titled "WorkState: …" | `workman_*`, titled "Workman: …" (`work://` URIs unchanged) |
| server instructions, prompts | "WorkState is the work tracker…", "WorkState project …" | "Workman …" |
| dashboard storage | `workstate.*` | `workman.*`; a 1.x value is moved once on first read |
| `server/plugin.mcp.json` | server `workstate`, `WORKSTATE_TOKEN` | `workman`, `WORKMAN_TOKEN` |

**Database migration `WorkmanNames`.** The request path's role `workstate_app` is replaced by `workman_app`:
created if missing, given this database's grants and default privileges as TenantRls gave them, and the old
role loses them here and is dropped once no database of the cluster grants it anything (a role is the
cluster's, so it is not renamed under another 1.x database). The functions `workstate_hidden_projects`,
`workstate_project_access` and `workstate_bump_access_seq` become `workman_*` (the policies and triggers hold
them by oid and are unchanged); `doc_block_revise` reads `workman.actor`; the journal notifies on channel
`workman`. A client registered under 1.x gains the `workman.*` scope permissions beside its old ones. Earlier
migrations are unchanged and still create the 1.x names, so a fresh database and a 1.2.0 one take the same
path. Down restores the 1.x names.

**What you do.** Rename every `WORKSTATE_*` variable in your `.env` to `WORKMAN_*` and every `WorkState__*`
override to `Workman__*`. The data-protection keys change purpose, so every OAuth client and dashboard
session **signs in again**; that is expected. The OAuth signing and encryption keys a 1.x server stored can no longer be
unprotected, so the server replaces them on its first start instead of failing every request. Clients that still send `X-WorkState-*` headers must send
`X-Workman-*`. The image `ghcr.io/getsupraman/workman`, the `work_*` tools, `work://` URIs, `/api/v1/*`,
`/api/stream`, `/mcp`, the well-known paths and the agent-session mint and its body are unchanged.

### Plugin 2.0.0 — every machine-facing name is `workman`

Plugin 2.0.0 goes with server 2.0.0 and needs it: it sends only the 2.0 names. The 1.x `workstate` names
that 1.2.0 still accepted are gone, with one exception, the OAuth scope aliases (below).

- **`/workman:setup`** (`setup.sh` / `setup.ps1`): the home is `~/.workman` (`%USERPROFILE%\.workman`),
  the compose project and volume are `workman` / `workman-db-data`, the `.env` it writes has `WORKMAN_*`
  keys and `POSTGRES_DB` / `POSTGRES_USER` `workman`, and the registration is `workman`. A `workstate`
  registration is still moved to `workman`, now with its `X-WorkState-*` headers renamed `X-Workman-*`
  and its `headersHelper` pointed at the new home.
- **The pull-only `compose.yml`**: project `workman`, `Workman__*` settings, `ConnectionStrings__Workman`,
  `WORKMAN_*` variables, database and user `workman` by default.
- **`session-header.mjs`** sends `X-Workman-*` only; **`find-shadows.mjs`** checks `workman` only.
- **The agents MCP**: `agent_launch`'s parameter is `workman: {role, env_file, url, ttl_hours, grants}`
  (was `workstate`); an `env_file` is read for `WORKMAN_CONTROLLER_TOKEN` / `WORKMAN_ORCHESTRATOR_TOKEN`;
  the structured results carry `workman` instead of `workstate`; the OAuth sign-in asks for
  `workman.sessions offline_access`; the grant store is `~/.workman/grants.json` (`WORKMAN_GRANTS_FILE`);
  the state directory is `workman-agents` only (an old `rlarch-agents` is no longer read); a launched
  agent gets `mcp__workman` and no special handling of `mcp__workstate`.
- **`server/deploy/multi-deploy`**: project, volume and Traefik router/service/middleware names
  `workman`; `WORKMAN_*` keys in `compose.yml` and `gen-env.sh` (default version 2.0.0); `backup.sh`
  dumps with the container's own `POSTGRES_USER` / `POSTGRES_DB`; the systemd units are
  `workman-backup.service` / `.timer`.
- Skills and docs use the 2.0 names in every instruction and example.

### Upgrading from 1.x to 2.0

Server 2.0 reads none of the 1.x `workstate` names. For clients, scripts and deployments:

- Environment: every `WORKSTATE_<X>` is `WORKMAN_<X>` (`WORKMAN_PUBLIC_URLS`, `WORKMAN_HOSTS` unchanged);
  settings `WorkState:*` / `WorkState__*` are `Workman:*` / `Workman__*`, and `ConnectionStrings:WorkState`
  is `ConnectionStrings:Workman`.
- The home is `~/.workman`, the compose project and volume `workman`, the registration `workman`.
- Headers: send `X-Workman-*`; `X-WorkState-*` is ignored, and `X-WorkState-Api` is no longer sent.
- OAuth scopes are `workman.read` and `workman.sessions`; **`workstate.read` and `workstate.sessions`
  stay accepted as aliases** until Supraman has switched.
- **Everyone signs in again**: the key ring is new, so 1.x cookies, OAuth grants and refresh tokens no
  longer open the server (`/mcp` → Authenticate; `agent_signin` again for the agents MCP).

## [1.2.0] — 2026-11-02

Server 1.2.0, plugin 1.1.0. No database migration since 1.1.4.

### Changed — the machine-facing names are now `workman`

The product has been Workman for a while; the names machines use said `workstate`. They now say
`workman`, and **the old names keep working until server 2.0 and plugin 2.0** — no earlier release
removes them.

| What | Was | Is | Old name until 2.0 |
|---|---|---|---|
| MCP `serverInfo.name` | `workstate` | `workman` | the name is the server's own; a client may still register it under any name, `workstate` included |
| registration `/workman:setup` writes | `workstate` | `workman` | an existing `workstate` registration keeps working, and setup moves it |
| tools as a session sees them | `mcp__workstate__work_*` | `mcp__workman__work_*` | follows the registration's name |
| request headers: `Agent`, `Session`, `Launched-By`, `Origin`, `Cwd`, `Foreman-Role`, `Client`, `Write` | `X-WorkState-*` | `X-Workman-*` | read under both names; when both arrive, `X-Workman-*` wins |
| response header on `/api/v1/*` and `/api/stream` | `X-WorkState-Api: 1` | `X-Workman-Api: 1` | sent under both names |
| the agents MCP's `--mcp-config` for a launched agent | server `workstate` | server `workman` | a launched agent with a minted token is denied `mcp__workstate` |

Not renamed: the `work_*` tools, `work://` URIs, the `WorkState.*` projects, the `WorkState__*` settings
and `WORKSTATE_*` variables, the database and its user, the compose project, cookies, the OAuth scopes,
`~/.workstate`, and `agent_launch`'s `workstate` parameter. The JSON body of `POST /api/agent-sessions`
is unchanged.

**What you do:** re-run `/workman:setup`. Without `--name` it finds the `workstate` registration in the
same scope and moves it to `workman` — same url, headers and `headersHelper`, by `claude mcp add-json`
then `claude mcp remove`, without printing its token — and installs the session helper that sends both
header families. Then, in each open Claude Code session, `/mcp` → **Reconnect**. A registration that
signs in with OAuth asks for a **new sign-in** under its new name: `/mcp`, pick `workman`,
**Authenticate**. That is expected; the old grant stays valid until it is revoked or expires.

Scripts and other clients that send `X-WorkState-*` headers, read `X-WorkState-Api`, or name the server
`workstate` in their own `--mcp-config` need no change before 2.0.

### Added — one server, several public URLs

- `WorkState:PublicUrls` (environment `WorkState__PublicUrls`, or `WORKMAN_PUBLIC_URLS`): further origins
  the server is reached on, comma-separated, after `WorkState:PublicUrl`. Each origin is its own OAuth
  issuer and resource; a request is answered as the origin its `Host` names, and a host that names none
  as the first. A token signed in at one origin keeps working there when another origin is added, and
  is refused at the other (its audience is per origin). An entry that is not an absolute http(s) URL
  stops the host from starting.
- `server/deploy/multi-deploy/compose.yml`: `WORKMAN_PUBLIC_URLS`, and `WORKMAN_HOSTS`, the Traefik rule
  for both routers (unset, `Host(WORKSTATE_HOST)` as before).

### Fixed

- The dashboard lists an open phase or feature with no children under **Open**, and the progress
  rollups (dashboard and `/api/v1`) count it as one item instead of none.

### Plugin 1.1.0

- `/workman:setup` registers `workman` and moves a `workstate` registration (above); `-Agent`/`--agent`
  registrations send `X-Workman-Agent` and `X-WorkState-Agent`.
- `find-shadows.mjs` checks both names; `--name` may be repeated.
- `session-header.mjs` sends `X-Workman-Client` / `X-Workman-Agent` and their `X-WorkState-*` twins.
- `agent_launch` writes a `workman` server into the agent's `--mcp-config` and allows `mcp__workman`.
