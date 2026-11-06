# 14 — Launching agents through a tool, with per-session credentials

A controller launches a nested Claude Code session with one tool call, and that session gets its own
Workman credential: a `wss_` token minted for its name and role. The model never sees the token.

---

## 1. The shape

```
agent_signin() ──▶ OAuth (PKCE, loopback redirect) to the server in ~/.workman/server.json,
                   scope workman.sessions offline_access; refresh token kept in ~/.workman/grants.json

controller model ──▶ agent_launch(name, brief, repo_root, controller, permission_mode, …, workman:{role, ttl_hours?, grants?})
                     │  plugin stdio MCP server "agents" (Node, no dependencies) — a host process
                     │  started by Claude Code for the session; the model never sees what it handles
                     ├─ reads the server from ~/.workman/server.json; refreshes an access token from grants.json
                     ├─ POST /api/agent-sessions {name, role, launchedBy, cwd, repo, ttlHours?, grants?} → wss_ token
                     ├─ removes a "workman" entry from ~/.claude/mcp-needs-auth-cache.json
                     ├─ writes <repo_root>/.claude/briefs/mcp-<name>.json  (that token; owner-only, must be gitignored)
                     ├─ writes <repo_root>/.claude/briefs/brief-<name>.txt (protocol header + brief)
                     ├─ runs  claude --bg --name <name> … --mcp-config <file> --permission-mode <mode> "<prompt>"
                     ├─ reads the session uuid from `claude agents --json`, PATCHes it onto the session
                     └─ returns {name, id, sessionId, brief, workman:{session, role, expiresAt}}   ← no secret

agent_stop(id|name) ──▶ claude stop [rm] · DELETE /api/agent-sessions/{id} (revoke) · delete mcp-<name>.json

agent_signout() ──▶ revokes the refresh token at the server and forgets it; every session minted under it
                    is refused on its next request
```

Why a **plugin** server and not a Workman tool: Workman runs in a container or on another host;
launching `claude` needs the host's CLI, the worktree on disk and the user's Claude login. A stdio
server that Claude Code starts for the session is a host process and has all three (doc 12 §1).

## 2. Workman: per-session tokens

Not under `/api/v1` (v1 is read-only by rule). Every call needs an OAuth access token for
`<origin>/api` with scope `workman.sessions`, or a `wss_` token with role orchestrator or controller.
A worker is `403`; the dashboard cookie is not a bearer.

| Call | Who | Does |
|---|---|---|
| `POST /api/agent-sessions` `{name, role, launchedBy?, origin?, cwd?, externalRole?, claudeSessionId?, ttlHours?, replace?, repo?:{remote?, root?}, grants?:["import"]}` | as above | creates/updates the `agent_session` row for `name`, issues a token `wss_<base64url 32 bytes>`, stores only its SHA-256, returns `201 {sessionId, name, role, token, expiresAt}` **once** |
| `PATCH /api/agent-sessions/{id}` `{claudeSessionId}` | its minter, or a controller | sets the Claude session uuid (history kept as an event, doc 11) |
| `DELETE /api/agent-sessions/{id}` | its minter, or a controller | revokes: `revoked_at = now()`, live claims of that session released (`release_reason = 'revoked'`) |
| `POST /api/agent-sessions/{id}/release` | a **controller** only | frees the name (doc 11 "Releasing a name"): the row is renamed `<name>~released~<id>`, its token revoked, `session_name_released` journaled; `200 {sessionId, name, releasedAs}`, `409` while it holds live claims |

Rules:
- **A role is never raised:** requested role ≤ the caller's role, else `403`. Worker < orchestrator <
  controller. An OAuth caller is a controller.
- **One live token per name:** a second mint for a name with an unrevoked, unexpired token is `409`
  unless `replace: true`, which revokes the old one first. `replace` keeps the **same row and
  `sessionId`** (one row per name across restarts, one continuous history) and swaps only the token;
  it does not release claims. A revoked or expired name mints again without `replace`, on the same row.
- **Launcher collision (doc 11):** a name held by a row with a different `launchedBy` is `409`, and
  `replace` does not override it. A mint for a header-named row with the same launcher attaches the
  token to that row. **Takeover:** with `replace: true`, a mint whose `launchedBy` is set takes a row
  whose `launched_by` is NULL only when the row's `claude_session_id` is set and equals the mint's
  `claudeSessionId`; the row then records the launcher (journal `session_launcher_taken_over`). Any
  other mismatch stays `409`, and its message names the release path above.
- `externalRole` fills `external_role` exactly as `X-Workman-Foreman-Role` does.
- **Grants:** `grants` is a set of capabilities stored on the row (`agent_session.grants`); an unknown
  name is `400`, and `import` on a `worker` role is `400` — `work_import` writes plan items, an
  orchestrator's act. Each mint sets the grants anew, so a `replace` without `grants` drops them. The
  bearer handler adds one `workman:grant` claim per grant; `import` shows `work_import` (doc 01 §6).
- **The minter:** a controller may PATCH or DELETE any session. Otherwise a session belongs to the
  session that minted it, and only while its role is at most the caller's.
- **A user's sessions:** every credential names a user — the OAuth user, or the user who minted the
  `wss_` session. For PATCH, DELETE and `release` alike, a member changes only the sessions that user
  minted; an admin of the tenant (membership role `admin`) or a system administrator, any session in
  it; otherwise `404`, with the same body as an unknown id, so the answer never says another user's
  session exists. This check runs before the minter rule.
- **Revoke** also returns the released claims' `in_progress` items to `ready` and journals
  `claim_revoked`, as the expiry sweep does for an expired claim.
- Revoked, expired and unknown tokens are refused on `/mcp` itself (HTTP 401), before any tool runs.
- **Expiry:** `ttlHours` (default 72, 1..720) is an **idle window**, stored per row. Each successful
  authentication slides `token_expires_at` to now + that window, written at most once a minute per
  token; there is **no hard cap**, so a token in use never lapses. An unused token lapses when its
  window passes, and an expired or revoked token is never renewed. `expiresAt` in the mint response is
  the current expiry, not a deadline.
- **Repo:** `repo:{remote?, root?}` is the repository the session works in (doc 02 `project_repo`),
  stored normalised on the row; that session's `work_brief()` with no scope and no `repo:` opens the
  project bound to it. `agent_launch` computes it from `repo_root` with `git -C <repo_root> remote
  get-url origin` and `git -C <repo_root> rev-parse --path-format=absolute --git-common-dir` (argv,
  no shell), so a worktree sends its main checkout's root; a repo with no origin sends `root` only.
- **Authentication:** the `WorkmanSessionToken` scheme accepts a `wss_` token by hash lookup (not
  revoked, not expired). Its principal carries the granted role (controller ⊇ orchestrator ⊇ worker),
  the session and the user — `CallerSession` takes the session from the principal and ignores an
  `X-Workman-Agent` header on that request. The name is bound to the credential, not asserted by a
  header (doc 12 §5).
- **OAuth mints:** the access token's audience is `<origin>/api` and its scopes include
  `workman.sessions` (doc 10 rule 5); without the scope it is `403`, and a `/mcp` token is `401`. The
  session is the user's (`agent_session.user_id`): its name is unique among their own sessions in the
  tenant (doc 11), and a `wss_` token it mints records the same user. The row records the user's name
  (`minted_by_subject`) and the OpenIddict authorization (`minted_by_authorization_id`); a `wss_` mint
  sets both to null. **A `wss_` token works only while its user may enter its workspace**, checked in
  the hash lookup: the membership removed or the user disabled, it is refused (`401`) on its next
  call. **A `wss_` token minted under an authorization works only while that authorization is
  valid**, checked on every use with no cache: revoking it — revoking its refresh token at
  `/connect/revoke`, which is what `agent_signout` does (RFC 7009 §2.1) — or pruning it ends the
  session's token at its next call (`401`). Revoking an access token alone does not.

## 3. The `agents` stdio server (plugin)

`plugin/.mcp.json` → server `agents`: `node ${CLAUDE_PLUGIN_ROOT}/skills/agents/server/agents.mjs`.
Tools appear as `mcp__plugin_workman_agents__<tool>`. No npm dependencies; plain ES modules with
`// @ts-check`. Speaks MCP over newline-delimited JSON-RPC on stdio.

Every tool finds the server in `~/.workman/server.json` (`home.mjs`); a missing or unusable file is a
refusal that names `/workman:setup`. There is no default url and no url parameter.

### `agent_signin` / `agent_signout` — no parameters

`agent_signin` signs in once per server, in the browser: RFC 9728 discovery at
`<origin>/.well-known/oauth-protected-resource/api`, dynamic client registration, PKCE S256 with an
RFC 8252 loopback redirect on `127.0.0.1`, scope `workman.sessions offline_access`. It opens the
system browser, waits up to 5 minutes, and keeps the client id and refresh token in
`~/.workman/grants.json` (mode 600). The result carries the URL to open by hand, never a token.
`agent_signout` revokes the refresh token at the server and forgets it.

### `agent_launch`

| Param | Type | Default | Maps to |
|---|---|---|---|
| `name` | string | required | `--name`; the `SendMessage` address |
| `brief` | path | required | a file inside `repo_root`; the protocol header is prepended and the result written to `.claude/briefs/brief-<name>.txt` |
| `repo_root` | path | required | must be a git working tree (refused otherwise); the session's cwd |
| `controller` | string | required | `"<name> [<6-hex ref>]"` (refused without the ref) — goes into the protocol header |
| `permission_mode` | `acceptEdits\|auto\|bypassPermissions\|manual\|dontAsk\|plan` | `auto` | `--permission-mode` |
| `toolset` | `none\|readonly\|fix` | `fix` | an `--allowedTools` profile defined in `header.mjs`, plus `mcp__workman` when `workman.role` is not `none` (else the first `work_*` call parks the agent under `acceptEdits`) |
| `allowed_tools` | string[] | `[]` | merged into `--allowedTools` |
| `disallowed_tools` | string[] | `[]` | `--disallowedTools` |
| `add_dirs` | path[] | `[]` | `--add-dir` |
| `model` | string | — | `--model` |
| `effort` | `low\|medium\|high\|xhigh\|max` | — | `--effort` |
| `settings` | path or JSON string | — | `--settings` |
| `append_system_prompt` | string | — | `--append-system-prompt` |
| `workman` | `{role: worker\|orchestrator\|controller\|none, ttl_hours?, grants?: ["import"]}` | `{role: "worker"}` | §1/§2; `none` mints nothing and passes no `--mcp-config`; `grants` goes to the mint as is — the server enforces who may hold one |
| `dry_run` | bool | false | validates, writes the brief and returns the argv and the config it would write (token shown as `<minted>`); mints and spawns nothing, but needs the `agent_signin` grant |

Every variadic flag is closed by the next flag and the prompt goes last. Arguments are passed as an
argv array (no shell). `mcp-<name>.json` must be gitignored in `repo_root` (`git check-ignore`), or
the launch is refused. If `workman.role` is not `none` and minting fails (not signed in, server
down, 4xx), the launch is **refused** with the reason — never launched without its own credential.
If the spawn fails after a mint, the token is revoked and its file deleted.

The config file holds a server named `workman` with a literal `Authorization: Bearer wss_…` header:
it replaces the user-scope registration in that session, and a `--bg` session expands no `${VAR}`
(doc 12 §4). Before the spawn, `agent_launch` removes a `workman` entry from Claude Code's
`mcp-needs-auth-cache.json`, which would otherwise make the worker skip the server for 15 minutes.

### `agent_stop` `{id | name, remove?, keep_brief?}`

`claude stop` (and `claude rm` with `remove`), revoke the minted token at the server recorded at
launch, delete `mcp-<name>.json` always (it holds a secret), and the brief unless `keep_brief`. State
of launched agents is kept in `${CLAUDE_PLUGIN_DATA}` (name, id, repo_root, server url, Workman
session id, config path — never the token).

### `agent_list`

`claude agents --json` joined with that state: name, id, status, waitingFor, repo_root, Workman
session and role; sessions not launched by `agent_launch` are listed too, marked as such.

### Results

Plain text plus structured content; refusals are tool results with `isError: true` that say what to
fix. No result ever contains a token.

## 4. Hand-started sessions

A session a person starts uses the user-scope OAuth registration and is named through the session
helper and `work_session(name)` (doc 11, doc 12 §5). Supraman mints for the sessions it starts with
its own OAuth sign-in, by the same endpoint (§2).

## 5. Verification the controller runs before release

1. A real `agent_launch` from this session with `permission_mode: "acceptEdits"`, `workman.role:
   "worker"`: the child's `work_brief` shows no "session unnamed" line; its writes are recorded under
   its name with `run_by = worker`; it cannot see `work_status`.
2. `agent_stop` → the token is revoked (a call with it is 401) and the config file is gone.
3. No token in: the tool result, the brief, the controller's transcript, `claude agents --json`.
