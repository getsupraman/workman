# Changelog

All notable changes to the Workman server and the `workman` plugin are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). The server's version is `serverInfo.version`; the plugin's is
`plugin/.claude-plugin/plugin.json`.

## [Unreleased]

- Plugin 1.2.0 (wording only): the skills, the agents MCP and the setup scripts say Workman instead of WorkState in their prose and printed messages, and so do the docs; no machine-facing name changes.
- Server source: the .NET projects, namespaces and types are `Workman.*` (`server/Workman.slnx`, `Workman.Api.dll` in the image). No behaviour, setting or stored name changes.

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
