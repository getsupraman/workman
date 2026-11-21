# 11 — Sessions as first-class records

Reviewed by autowork-7c (Foreman) 2026-10-23; its one blocker (key named sessions by name, not by connection) is folded in below.
Corrected while implementing (aw-m4, 2026-10-23): the transport is stateless, so the connection and
self-declaration tiers were dropped — sessions are header-only. See "Why headers only".
Unit `mature/agents`. Problem, measured on the live store 2026-10-23: all 301 events and all 26 claims
are signed `controller` (or `orchestrator`). Two controller sessions (skills-5d, autowork-7c) share one
user-scope token, so the server cannot tell who did what, and a claim cannot say who holds a unit.

## Principle
The **role** still comes from the bearer token and nothing else (doc 01 §6: a worker can never record
an orchestrator's evidence). The **session** is a second, independent fact: *what the calling session
is called*. Identity never grants a role; it labels, and it is the unit the tree rule counts in.

## Where the identity comes from: the registration headers, and nothing else
- `X-Workman-Agent: <name>` — the session's name; with the workspace and the credential's user, the
  row key.
- `X-Workman-Session: <claude session id>` — kept on the row; a change is history (below).
- `X-Workman-Launched-By: <name>` — the collision rule (below).
- `X-Workman-Origin` (free text, e.g. "foreman/orion/Orion"), `X-Workman-Cwd`,
  `X-Workman-Foreman-Role` (platform|repo|worker, kept when it differs from the token role).

An OAuth caller sends them from its registration (`claude mcp add --header`, or the session helper
below). They ride on every request, so they survive
reconnects, resumes and compaction.

**Or from a minted token (doc 14 §2).** A launcher can mint a session instead of naming it:
`POST /api/agent-sessions {name, role, launchedBy?, origin?, cwd?, externalRole?, claudeSessionId?,
ttlHours?, replace?, repo?}` creates (or reuses) the row for `name` in the minting credential's own
tenant — never a workspace the request names (doc 08 §4) — and returns a `wss_` token once, whose calls
act in that workspace;
`repo` is the session's repository, which its bare `work_brief()` resolves to a project (doc 02);
`ttlHours` (default 72) is an idle window that every use renews, with no hard cap. A call
with that token *is* that session: the name comes from the credential, `X-Workman-Agent` is ignored,
and the other headers still fill the row as above. `externalRole` fills `external_role` exactly as
`X-Workman-Foreman-Role` does. The collision rule below holds for a mint too: a name held under a
different `launched_by` is refused (409), and `replace` does not override it — with two exceptions, the
**takeovers**. The first (2026-10-28, unit `shared-v1/name-takeover`): a mint with `replace: true` and a non-null
`launchedBy` may take a row whose `launched_by` is NULL when that row's `claude_session_id` is
non-null and equals the mint's `claudeSessionId` (ordinal, both trimmed). That is a hand-named session
later resumed by a launcher, proven by its Claude session id. The row records the new `launched_by`,
keeps its id, claims and history, its old token (if any) is replaced as in any `replace`, and the
journal gets `session_launcher_taken_over` (`{from: null, to, claudeSessionId}`) on the session. The
second, **`takeoverFrom`** (server 3.0.2, unit `v3-0-2/launcher-takeover`), is a launcher renamed under
the same user (`foreman/x` → `supraman/x`): a mint with `replace: true`, a non-null `launchedBy` that
differs from the row's, and `takeoverFrom` (trimmed as `launchedBy` is) equal to the row's current
`launched_by` (ordinal) takes that row the same way, and the journal gets `session_launcher_taken_over`
with `{from: <old>, to: <new>, claudeSessionId}`. Rows are per user, so the row found is already the
caller's own; another user's mint of the name makes their own row. A row whose `launched_by` is NULL is
never taken with `takeoverFrom`. Every other case stays 409: no id on the row, ids that differ, `replace`
false, two different non-null launchers without a matching `takeoverFrom`, and a mint with no launcher
onto a launcher's row. The 409 names `takeoverFrom` and the release path below.
`replace: true` keeps the row, its id, claims and history, and swaps only the token. `DELETE /api/agent-sessions/{id}` revokes
the token and releases the session's live claims (`release_reason = 'revoked'`; the items go back to
`ready`, as an expired claim's do). Header-named sessions keep working for anything that cannot mint.

**A user's sessions are theirs (2026-10-28, m51).** A session named or minted with an OAuth token, or
with a `wss_` token a user minted, records that user (`agent_session.user_id`). The name is unique per (workspace, user) —
`NULLS NOT DISTINCT`, so rows with no user share one name per workspace. Two users of one tenant may
each mint `worker-1`; one user cannot hold it twice (409). The user's token works only while that
user may enter the workspace: their membership removed or their account disabled, it fails on its next
use. `PATCH` / `DELETE` / `release` keep the role rule above, and a credential that names a user adds
one: a member changes only the sessions they minted; an admin of the tenant, or a system
administrator, any session in it.

**Revoked with the app that minted it (2026-10-30, m65).** A session minted with an OAuth token records its
authorization (`minted_by_authorization_id`) and authenticates only while that authorization is valid. When the
user revokes the app on the dashboard's Account page (`POST /api/session/apps/{id}/revoke`, doc 10), each live
session minted under it is revoked as `DELETE /api/agent-sessions/{id}` revokes one: its row reads revoked and its
claims are released and journalled, with the user as the actor and `human` as the role. A client's own sign-out at
`/connect/revoke` ends the authorization the same way, and its sessions stop authenticating.

**Or by naming itself (2026-10-26, unit `mature/session-names`).** A hand-started session shares the
user-scope OAuth registration, so it has no header of its own — and a fixed `X-Workman-Agent` in
`~/.claude.json` would give every session the same name. The registration's `headersHelper`,
`session-header.mjs` (installed by `/workman:setup`), sends `X-Workman-Client:
<host>/<pid>/<start epoch s>` of the nearest Claude Code ancestor process — found in the process table,
never from `CLAUDE_PID`/`CLAUDE_JOB_DIR`, which are inherited from whoever started the process — and
`X-Workman-Agent` too when `claude agents --json` lists that pid with a name (background sessions;
a fresh interactive one may not be listed yet). `work_session(name, claude_session_id?)` binds the
request's key to the row for `name` (created as a header would create it), moving the key off any
other row. It is refused for a name another key used within the lease window (the claim TTL), for a
minted session's name, and under the launcher rule below. `agent_session.client_key` is unique per
workspace (migration `SessionClientKey`).

**Resolution order, per request.** (a) a minted `wss_` token → its session; both `X-Workman-Agent`
and `X-Workman-Client` are ignored. (b) `X-Workman-Agent` → the session by name; with
`X-Workman-Client` also present, that key is bound to it. (c) only `X-Workman-Client` → the
session the key is bound to, if any. (d) otherwise no session.

A call that resolves to no session has **no session**: it writes `session_id` NULL, renders as its role
exactly as before doc 11, and keeps today's per-tree behaviour (a sessionless claim holds its tree
alone). `work_brief` tells such a caller, in one line under its header, what to do: with a client key,
the `work_session(name:…)` call; without one, the setup that installs the helper. Write tools say
nothing.

### Why headers only
The MCP SDK (ModelContextProtocol 2.2.0) defaults the Streamable HTTP transport to
`HttpServerSessionMode.Stateless`, per the 2026-07-28 protocol revision (SEP-2567 removed
`Mcp-Session-Id`, SEP-2575 the `initialize` handshake). There is no connection id to key an unnamed
call by, and each request is served by a fresh server context — so a name declared on one call
(`work_brief(agent:)`) would bind to nothing, and the next call would be anonymous again. Only what
travels on every request can name a session. No tool parameter was added (tools/list is unchanged).

## The row key survives a resume
`claude --bg --resume <id>`, auto-compaction and the daemon's respawn keep the name and the headers.
- **A session is keyed by (workspace, user, name)** — the user is the credential's — so a resumed session lands on its own row and keeps its claims and its tree. A header or a
  client key resolves only among the caller's own user's sessions.
- **Name collisions:** names are labels; Foreman guarantees uniqueness among its own sessions. A name
  already held by a row with a *different* `launched_by` (including one set against one unset) is
  refused with a teaching REFUSED, and nothing runs.
- `claude_session_id` changes under one name are kept as history (a `session_claude_id_changed` event),
  not overwritten silently — Foreman detects copies that way.

## Releasing a name
A row with no Claude session id cannot prove who it was, so no launcher can take it over. The operator
frees its name instead: `POST /api/agent-sessions/{id}/release` (a controller `wss_` session, or an OAuth
token with `workman.sessions`; doc 14 §2), or **Release** on the dashboard's Sessions page (cookie,
`POST /api/sessions/{id}/release`, doc 10). The row is **not deleted** — claims, events and evidence
point at it. It is renamed to `<name>~released~<its id, 32 hex>`, which its own id makes unique; its
token is revoked (`revoked_at`), its client key is let go, and `session_name_released` (`{name,
tombstone}`) is journaled on it. The next mint or header under the name creates a new row. A session
that still holds live claims of its own is refused (409, naming the refs): it may still be working, and
`DELETE /api/agent-sessions/{id}` is the existing way to release its claims first. A claim another
session took *for* it (see "Claims for delegated work" below) does not refuse the release: it is
released with it (`release_reason = 'session_released'`, a `claim_session_released` event), and its
item returns to `ready` as the expiry sweep returns one. A row already
released is 409. The header path is unchanged: a header never takes over a name, and the release is
the operator's alone.

## Data (one migration, additive)
`agent_session`: id (uuid v7), workspace_id, name (unique per workspace; per workspace and `user_id`
since m51, migration `AgentSessionUser`), claude_session_id, role (from
the token at the last call — recorded, never consulted), external_role, origin, launched_by, cwd
(header, else the first claim's tree), tree (last claimed), client_name, client_version (from
`initialize` clientInfo when the transport has it — stateless requests usually do not),
first_seen_at, last_seen_at.
Nullable `session_id` FK (ON DELETE SET NULL) added to: `event`, `claim`, `verification`, `report`,
`note`, `assumption`. Existing rows stay NULL ("before sessions"). No backfill invented.
The tree lock becomes two constraints: `claim_one_writer_per_tree` (unique, now only over claims with
no session) and `claim_one_session_per_tree` (an exclusion constraint, `btree_gist`: live write claims
on one tree must share one session; a sessionless claim counts as its own holder). Down releases all
but the oldest live claim per tree, then restores the old index.

## Behaviour changes
- Every event/claim/verification/report/note/assumption written in a session carries its `session_id`;
  `last_seen_at` is touched on every tool call and resource read.
- **Lease renewal:** any call from the claiming session — a read included — renews its live claims'
  lease (today a 30-minute lease lapses under a multi-hour unit and the inbox shows "claim expired" for
  healthy work). A claim taken for another session renews on the calls of either session; see below.
- **One writer per tree means one *session* per tree:** the same session may claim several items in the
  tree it already holds (today a controller cannot claim two tasks one nested agent works on in one
  worktree). A *different* session is still refused, naming the holder by name.
- Text surfaces print the session where they print an actor today: claims in `work_tree`/`work_get`
  rows and the worker brief ("held by aw-m1"), refusals ("held by skills-5d"), history rows, the
  expired-claim attention line. A sessionless writer prints as its role, as before.

## Claims for delegated work (mcp-fit/6)
A controller claims an item, and a nested session it launched does the work, for minutes or for hours.
Renewal on the claiming session's calls alone let the lease lapse while the worker was busy, so the
attention filled with "claim expired" and teams set `in_progress` only at the close. Now the
controller claims *for* the worker:

`work_status(ref, "in_progress", lease: 240, for_session: "wk-a")`

- **`lease`** is minutes, 1..480 (C346). Above 480 is refused with the cap and the call that fits, and
  nothing is written. Without it the workspace's `claim_ttl_minutes` applies (30 by default).
- **`for_session`** names a live session of the workspace and the caller's user, by the name it was
  launched under (`agent_launch`). An unknown, released (tombstoned) or revoked name is refused, and
  nothing is written.
- Both are an orchestrator's, controller's or human's (C344): a worker passing either is refused and
  nothing is written. A worker's own plain `in_progress` is as it was. Each may be given alone: `lease`
  alone sets the caller's own claim's lease; `for_session` alone takes the default lease.
- **The holder is the named session.** `claim.session_id` is that session, so Working now, the board,
  `work_get`, `work_tree`, refusals and the expired-claim line print its name, with the claimer after
  it: `wk-a (for ctl-a)`. `claim.claimed_by` (the claimer's label) and `claim.claimed_by_session_id`
  record who claimed it, and `claim.lease_minutes` the lease (NULL: the workspace's). Migration
  `DelegatedClaims`, additive.
- **Renewal.** Any call from the holding session or from the claiming session renews the claim, to its
  own lease, not the default. A call from any other session does not.
- **Release.** Releasing the named session's name (above) or revoking it (`DELETE`, `agent_stop`)
  releases the claim and returns its item to `ready`. The claiming controller, as any orchestrator, can
  move the item out of `in_progress` at any time, which releases the claim as every move does. Its plain
  `work_status(ref, "in_progress")` takes the item back, in place or after such a move, and makes it
  the holder again with the default lease.
- **Trees.** The claim is the named session's on its tree (`claim_one_session_per_tree`, unchanged): on
  that tree, while the worker holds it, another session — the controller included — claims nothing
  else. Name the worker's own worktree with `tree:` when it has one.

## Read API (doc 10) consequences — not in this unit
`Claim.session` and every journal event's `session`: `{ id, name, role, client, launchedBy|null,
claudeSessionId|null, lastSeenAt }`. `agentRecord` in the draft becomes `session`. New
`GET /api/v1/sessions?active=true|name=|claudeSessionId=` — sessions with what each holds, for
Foreman's fleet rows ("holds m2/4") and Attach. Session shape adds `origin`, `externalRole`, `cwd`.
The dashboard's journal `actor` and lease holder already show the session name where there is one.

## Not in this unit (decision)
- Authenticating the name: a header-named session can claim any name; it is a label, like a git
  author. A minted token (doc 14 §2, Foreman need #4) binds the name to the credential instead.
- Killing/attaching sessions from Workman: Foreman's job, using `claudeSessionId`.
