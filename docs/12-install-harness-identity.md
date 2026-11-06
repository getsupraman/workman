# 12 — Install, harness and identity: Workman from a fresh PC

What Claude Code's MCP support does with a Workman server, where each part of an install lives, and how
a session gets its identity. Every statement is tagged with how it is known:

- **[measured]** observed on a machine (Claude Code **2.1.309**, Windows 11)
- **[code]** read in this repo
- **[doc]** read in Claude Code docs (`code.claude.com/docs/en/…`) or the MCP specification
  (`modelcontextprotocol.io`), with the page named
- **[not documented]** looked for, not found — treat as unknown, not as "no"

---

## 0. The short answers

| Question | Answer |
|---|---|
| Does the harness keep an MCP session between calls? | **No.** Claude Code 2.1.309 opens with the **2026-07-28** revision (`server/discover`), which removed `Mcp-Session-Id` and `initialize` **[measured]** **[doc: SEP-2567, SEP-2575]**. Workman's SDK (C# 2.2.0) is stateless **[code]**. Every request stands alone. |
| Who carries the credential? | **The harness, never the model.** Claude Code attaches the server entry's `headers`, the output of its `headersHelper`, or its OAuth access token to every HTTP request **[doc: mcp.md "headers and headersHelper", "OAuth"]**. The model only calls `work_*` tools. |
| How does a person or a hand-started session sign in? | **OAuth.** `/workman:setup` registers `workman` at user scope with no `Authorization` header; the user runs `/mcp` → **Authenticate** once and Claude Code keeps the grant **[code: setup.sh]**. |
| How does a launched agent sign in? | **A minted `wss_` token.** `agent_launch` mints one per session and writes it into that session's own `--mcp-config` **[code: plugin/skills/agents/server]**, doc 14. |
| What names a session? | A minted token's session is its credential's name. An OAuth session sends `X-Workman-Client` from the session helper and is named with `work_session(name)` (or an `X-Workman-Agent` header) **[code]**, doc 11. |
| Where does every tool find the server? | `~/.workman/server.json`, `{"url": "<origin>", "mcpName": "workman"}`, written by `/workman:setup`. There is no default. **[code]** |

---

## 1. The moving parts, and where each one lives

| Part | What it is | Where it lives on a PC | Who puts it there |
|---|---|---|---|
| **Marketplace** `supraman` | a git clone of `github.com/getsupraman/workman` | `~/.claude/plugins/marketplaces/supraman/` **[measured]** | `/plugin marketplace add getsupraman/workman` |
| **Plugin** `workman` | `plugin/`: the skills and the `agents` stdio MCP server | `~/.claude/plugins/cache/supraman/workman/<version>/` **[measured]** | `/plugin install workman@supraman`; re-copied when `plugin.json` `version` changes |
| **Server pointer** | `~/.workman/server.json` — which Workman this machine uses | `~/.workman/` | `/workman:setup` (and Supraman's settings window), atomically |
| **Session helper** | `session-header.mjs`, the registration's `headersHelper` | `~/.workman/session-header.mjs` — a stable copy, since the plugin cache path changes per version | `/workman:setup` |
| **Agent sign-in** | the `agent_signin` OAuth grant (refresh token per origin) | `~/.workman/grants.json`, owner-only | `agent_signin` |
| **Client registration** | the `workman` MCP server entry: url, `headersHelper`, no header | `~/.claude.json` → `mcpServers.workman` (user scope) **[measured]** | `claude mcp add-json`, run by `setup.sh` |
| **Workman source** | .NET 10 API + MCP server + SolidJS dashboard, `server/` | optional — only to develop Workman or build the image yourself | the user, `git clone` |
| **Workman binaries** | `ghcr.io/getsupraman/workman:<version>` (amd64 + arm64), or built from a clone, plus `postgres:17` | Docker's image store | `setup.sh`: pulled by `~/.workman/compose.yml`, or built by `<clone>/server/compose.yml` **[code: server/Dockerfile]** |
| **Workman data** | the Postgres database: every plan, claim, verification, journal | Docker volume `workman_workman-db-data`, the same for both composes | created on first `up`; schema applied by the API at startup; `--backup` / `--restore` |
| **Server configuration** | dashboard login, Postgres password, port, bind — no tokens | `~/.workman/.env`, or `<clone>/server/.env` (gitignored; template `server/.env.example`) | `setup.sh`, once, with random values; never overwritten |
| **Running service** | `http://127.0.0.1:5290` — `/mcp`, `/api/*`, dashboard, `/health` | compose project `workman`, `restart: unless-stopped` | Docker |

Nothing of Workman's server runs inside Claude Code: it is a service you run, or a hosted one, and
Claude Code only connects (doc 08 §1). A self-hosting PC needs **Docker**; the plugin's helper and
`agents` server need **Node**.

The plugin's `.mcp.json` declares only the `agents` server. It does not declare `workman`: a plugin
server is attempted by every session in every project, and its url differs per machine.

---

## 2. A fresh PC

1. `/plugin marketplace add getsupraman/workman`, then `/plugin install workman@supraman`. In the
   next session the skills are listed as `/workman:<name>` and the `agents` tools are there; there is no
   `workman` registration yet, so no `work_*` tools. `/workman:orchestrate`, `/workman:controller`
   and `/workman:plan` say so and point at `/workman:setup`.
2. `/workman:setup` (self-hosted) or `/workman:setup --url <origin>` (hosted or a team server) runs
   `setup.sh`: it writes the `.env` and starts the stack when local, copies the session helper, proves
   that `POST /mcp` without a token answers `401` with a `resource_metadata=` link that answers,
   registers `<origin>/mcp` at user scope with the helper and no header, writes `server.json`, and
   lists any local- or project-scope `workman` entry that would shadow it **[code]**.
3. In a new session: `/mcp` → `workman` → **Authenticate**, sign in in the browser, **Approve**. The
   `work_*` tools appear. MCP servers resolve at session start, so they never appear in the session
   that ran setup **[measured]**.

The model never reads the `.env` and never handles a token: the script hands the passwords to Docker,
Claude Code holds the OAuth grant, and `agent_launch` writes a minted token straight into a file.

---

## 3. The Workman MCP surface

**[code unless marked]** — full contract in docs 01, 05–07, 10, 11.

- Streamable HTTP at `POST /mcp`, ASP.NET Core, `ModelContextProtocol.AspNetCore` **2.2.0**,
  stateless; the SDK still answers older `initialize` clients, statelessly.
- Server instructions (`WorkmanServerInstructions`, doc 05) are delivered with discovery.
- Tools are gated by role: a tool a credential may not call is **absent** from `tools/list`, not
  refused (doc 01 §6). A worker sees six; orchestrator and controller see the planning and closing
  tools too.
- Before a tool runs, `ToolArguments` refuses undeclared or wrong-shaped arguments with the nearest
  real parameter, and `CallerSession` attaches the session (doc 11).

### Authentication — what the server believes

| Scheme | Credential | Establishes |
|---|---|---|
| `WorkmanSessionToken` | a minted `wss_` token, by hash: not revoked, not expired, its user still a member | the **session** (its name), its granted **role** and grants, and the user who minted it |
| OAuth | an access token issued by the server itself (`/connect/authorize`, doc 08 §4) | the **user** and the one tenant they approved; role controller; the session from `X-Workman-Client` / `X-Workman-Agent` |
| dashboard cookie | dashboard sign-in | a signed-in user in one tenant, for `/api/*` and the UI |

Every credential has a user. A request that ends `401` logs one `Workman.Auth.Challenge` line: method,
path, `X-Workman-Client`, `X-Workman-Agent`, `User-Agent`, whether an `Authorization` header was
present and why each scheme refused — never the value. `_meta.clientInfo` is not used: it says
`claude-code` for every session.

Next to MCP: `GET /api/v1/*` (doc 10), `/api/agent-sessions` (doc 14), `GET /api/stream` (SSE), the
dashboard at `/`, `/health`. Same port, same container.

---

## 4. What the Claude Code harness supports for MCP

| Topic | What is true | Source |
|---|---|---|
| Transports | `stdio`, `http` (alias `streamable-http`), `sse` (deprecated), `ws` | [doc: mcp.md "Transports"] |
| Protocol | Tries **2026-07-28** first (`POST server/discover`, `MCP-Protocol-Version: 2026-07-28`, `Mcp-Method`), falls back to `initialize` with `2025-11-25` when the server refuses | [measured] |
| Per-request identity it sends | `User-Agent: claude-code/2.1.309 (cli)`; `_meta["io.modelcontextprotocol/clientInfo"] = {name: "claude-code", …}` — **identical for every session** | [measured] |
| Session id between calls | none under 2026-07-28 | [doc: SEP-2567] |
| When it connects | at session start (unless a cached tool list defers it to first use); retries a remote first connect up to 3×, mid-session drops up to 5× with backoff; `/mcp reconnect` | [doc: mcp.md "Reconnect behavior"] |
| New servers mid-session | no — a registration made now appears in the **next** session | [measured] |
| Server instructions | the server's `instructions` arrive with discovery and are given to the model at session start | [code] [measured] |
| Scopes | local (`~/.claude.json` → `projects[path]`), project (`.mcp.json`, needs approval), user (`~/.claude.json`), plugin, managed | [doc: mcp.md "Scopes and storage"] |
| Same name in two scopes | local > project > user > plugin > claude.ai connectors; **the whole entry wins, fields are not merged** | [doc: mcp.md "precedence"] |
| `--mcp-config` vs a same-named user server | the `--mcp-config` entry **replaces** it | [measured] |
| `--mcp-config` with `claude --bg` | honoured | [measured] |
| Static `headers` | `${VAR}` / `${VAR:-default}` expansion in `url`/`headers` from the harness process environment | [doc: mcp.md ".mcp.json format and expansion"] |
| Env in a `--bg` session | **not inherited** from the launching shell, and the worktree's `.claude/settings.local.json` `env` does not feed header expansion either — so a `--bg` session's config carries literal values | [measured] |
| `headersHelper` | a shell command, run fresh at session start, on reconnect and after a 401/403; prints a JSON object of headers; 10 s timeout; overrides same-named static headers; receives `CLAUDE_CODE_MCP_SERVER_NAME`, `CLAUDE_CODE_MCP_SERVER_URL` (and `CLAUDE_PLUGIN_ROOT` for plugin servers) — **no session id or name** | [doc: mcp.md "headers and headersHelper"] |
| `headersHelper` in a `--bg` session | works — the header arrives on every request | [measured] |
| `headersHelper` process | runs as node ← shell ← the session's `claude` process; `CLAUDE_PID` and `CLAUDE_JOB_DIR` are inherited from whatever started that process and can name another session | [measured] |
| OAuth | RFC 9728 → RFC 8414 discovery, dynamic registration or `oauth.clientId`, tokens in the keychain/credentials file, refreshed automatically; **per server, not per session** | [doc: mcp.md "OAuth"] |
| Needs-auth cache | a server that answers `401` with no usable token gets an entry, keyed by server **name**, in `~/.claude/mcp-needs-auth-cache.json`; for 15 minutes every session skips connecting any server of that name, including one from `--mcp-config` | [measured] |
| Plugin `userConfig` | `sensitive: true` values go to secure storage; `${user_config.KEY}` substitutes into the plugin's MCP config, but `headersHelper` rejects `${user_config.*}` | [doc: plugins/manifest-reference.md] |
| Roots | answers `roots/list` with the launch directory (+ added dirs) — a server→client request a stateless server cannot easily make | [doc: mcp.md "Roots"] |
| Session name / id as env var | `--name` shows in agent view and `claude agents --json` only | [not documented] |

The harness owns the connection and its credentials and gives every session the same client
identity. The per-session levers it hands over are which config a session starts with and, inside
it, a literal header or a `headersHelper` command line.

---

## 5. Identity

The MCP 2026-07-28 revision has no sessions; servers "MUST NOT rely on prior requests" for identity,
`clientInfo` is self-reported and *"SHOULD NOT"* drive security decisions, and SEP-2567 names **the
authenticated principal** as the replacement **[doc: base protocol "Statelessness"; SEP-2567]**.
Workman follows that:

- **Role comes from the credential.** An OAuth sign-in is a controller; a minted token carries the
  role it was minted with, never higher than its minter's (doc 14 §2).
- **A launched agent's name is its credential.** `agent_launch` mints a `wss_` token for the session's
  name and writes `<repo_root>/.claude/briefs/mcp-<name>.json`, a `workman` entry with a literal
  `Authorization` header (a `--bg` session expands no `${VAR}`). That entry replaces the user-scope
  one, so the agent writes under its own name and role; the server ignores `X-Workman-Agent` on its
  requests.
- **A hand-started session is named through the helper.** OAuth gives one identity per user per
  tenant, not per session. `session-header.mjs` prints `X-Workman-Client` — host, pid and start time
  of the nearest `claude` ancestor in the process table — and, when `claude agents --json` lists that
  pid with a name, `X-Workman-Agent`. It reads no secret and prints `{}` on any failure.
  `work_session(name)` binds a name to that client key (doc 11).

---

## 6. Starting a nested Claude

**[code: plugin/skills/agents/]** — mechanics in that skill, discipline in `controller`, the tool in
doc 14.

1. The controller makes a **worktree + branch** per unit and claims the unit in Workman with `tree:`
   set to it.
2. It writes a **brief file** and calls `agent_launch`, which mints the session's token with the
   `agent_signin` grant for the server in `server.json`, prepends the READY / BLOCKED / DONE protocol
   header to the brief, removes a `workman` entry from the needs-auth cache, and runs
   `claude --bg --name <name> … --mcp-config <file> --permission-mode <mode> "<prompt>"` in the
   worktree, with argv and no shell.
3. Protocol: `READY <task>` → work → `BLOCKED <q>` (controller answers) → `DONE <report>`. The
   controller arms a watchdog (`watch-nested-agent.py`) via `Monitor`, verifies independently,
   merges, removes the worktree and calls `agent_stop`, which revokes the token and deletes its file.

### Never probe Workman

**Never run `claude mcp get`/`list`, nor start a `claude` process, to check Workman.** Each one
health-checks the server without a token; the `401` writes a `workman` entry to the needs-auth cache,
and for 15 minutes every session skips connecting a server named `workman` — `agent_launch` workers
included, which then start without `work_*` tools **[measured]**. `setup.sh --dry-run` reads the
registration's url from Claude Code's config file instead, and `server.json` says which server setup
registered.
