# Changelog

All notable changes to the Workman server and the `workman` plugin are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and versions follow
[Semantic Versioning](https://semver.org/). The server's version is `serverInfo.version`; the plugin's is
`plugin/.claude-plugin/plugin.json`.

## [Unreleased]

- Plugin 1.2.0 (wording only): the skills, the agents MCP and the setup scripts say Workman instead of WorkState in their prose and printed messages, and so do the docs; no machine-facing name changes.
- Server source: the .NET projects, namespaces and types are `Workman.*` (`server/Workman.slnx`, `Workman.Api.dll` in the image). No behaviour, setting or stored name changes.

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
