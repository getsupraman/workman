# 08 — Distribution, lifecycle, and the always-on frontend

Four questions from the owner, answered against what is verified rather than assumed.

> The product is **Workman** (dashboard, consent pages, MCP `serverInfo.title`). Every machine-facing
> name is `workman`: `serverInfo.name`, the registration `/workman:setup` writes (so `mcp__workman__*`
> tools), the `X-Workman-*` headers, the `WORKMAN_*` environment and `Workman:*` settings, the compose
> project and volume, the home `~/.workman`. A public URL is one OAuth issuer, and
> `WORKMAN_PUBLIC_URLS` adds more (§4).

---

## 1. Lifecycle — answer this first, the rest follows from it

**The transport decides who owns the process**, and we chose HTTP.

| | **stdio** | **Streamable HTTP** ← ours |
|---|---|---|
| Who starts it | Claude Code, per session | **you** |
| Who kills it | Claude Code, at session end | **you** |
| Concurrent agents | one process each | **one server, many clients** |
| Alive between sessions | ✗ | **✓** |
| Can host a web UI | ✗ — it dies with the session | **✓** |

So: **Workman is not "always running" because it is a plugin. It is always running because you
run it.** Claude Code only ever *connects*; it never manages the process.

Two consequences that were not obvious when the transport was chosen:

- **The parallel-orchestrator design needs this.** Doc 02 §2 has many orchestrators writing at once
  against one store, arbitrated by `claim_one_writer_per_tree`. Under stdio each session would get
  its **own process**, all pointed at one Postgres — which would still work, but there would be no
  stable host for a UI and no single place to watch from.
- **An always-on frontend is only possible because of it.** Under stdio the dashboard would exist
  only while an agent happened to be working, and would die mid-page-load when the session ended.
  The transport choice made months of frontend design viable by accident. Worth noticing.

---

## 2. What the marketplace distributes — the skills, and no server registration

**A plugin cannot ship a running server.** Three models, and only one survives contact with the
frontend requirement:

| | Model | Verdict |
|---|---|---|
| **(a)** | The user runs the stack (or uses a hosted one); `/workman:setup` registers it | ✅ **this one** |
| (b) | Plugin ships the .NET project; Claude spawns it per session over stdio | ✗ kills the frontend; and every session migrating a shared Postgres is a race nobody needs |
| (c) | stdio shim that proxies to the HTTP server, starting it if absent | ✗ over-engineered, and it hides a failure you want loud |

**The plugin's `.mcp.json` registers only the `agents` server** (`node …/skills/agents/server/agents.mjs`).
It carries no `workman` entry, because the plugin cannot know which server a user runs — a local
install, a team's, or the hosted `https://work.supraman.ai` — and a registration shipped in the plugin
would try to connect in every session of every repo, whether or not that project uses Workman.

`/workman:setup` (`plugin/skills/setup/assets/setup.sh`) writes both halves of the pointer:

- the registration, at user scope, with **no `Authorization` header** — the user signs in once
  through `/mcp` → Authenticate — and a `headersHelper` (`~/.workman/session-header.mjs`) that sends
  the session's `X-Workman-Agent` name:

  ```
  claude mcp add-json --scope user workman '{"type":"http","url":"<origin>/mcp","headersHelper":"node \"<home>/.workman/session-header.mjs\""}'
  ```

- **`~/.workman/server.json`** = `{"url": "<origin>", "mcpName": "workman"}`, which the agents
  server reads for the server's address. There is no default address: without that file its tools
  refuse and say to run `/workman:setup`.

**Plugin tools are namespaced `mcp__plugin_<plugin>_<server>__<tool>`**, not `mcp__<server>__` —
so the agents server's tools are `mcp__plugin_workman_agents__agent_launch` and the rest. Any skill
or profile that names a tool explicitly has to use that longer form.

### Version skew

The plugin and the server **version separately**: the plugin updates through the marketplace, the
server when you pull or rebuild it. A skill that assumes a tool exists breaks against an older server,
**silently**, because a missing tool is simply absent from `tools/list` (measured — doc 01 §6).

`McpServerOptions.ServerInfo.Version` (`3.0.0`) is **bumped deliberately**, and the skills state the
minimum they need, so the failure is a sentence rather than a mystery. `CHANGELOG.md` records what
each server and plugin version carries and which migrations it runs.

---

## 3. Should the skills use it? — Yes, and as a **hard** dependency

> **SCOPE CORRECTED 2026-09-22 — it is two skills, not three.** This section and doc 03's Phase 6
> both say *“rewrite `orchestrate`, `controller` and `/workman:agents`”*. Measured: `/workman:agents`
> contains **zero** references to a ledger, `PLAN.md`, `CONVENTIONS.md` or the orchestration
> directory. Its nine sections are launching a session, addressing it, the READY/BLOCKED/DONE
> protocol, steering, watching, wrapping up, concurrency, restart recovery and writing the brief.
> **None of that is bookkeeping a store can hold** — it is the mechanics of running a process.
>
> It needs at most one sentence pointing at where binding rules now live. The rewrite is
> `orchestrate` and `controller`.

The tempting answer is dual-mode: use the store if present, fall back to markdown. **Don't.** That is
exactly the "two overlapping artifacts, you maintain both and trust neither" failure `orchestrate`
warns about — and the file path would rot, because nobody would exercise it.

**Recommended shape:**

- **Hard dependency, with a preflight.** The skill's first instruction is `work_brief`. If the server
  is unreachable, the skill says **the one command that starts it**. That is
  `/workman:agents`'s *"every prohibition needs a route"* applied to a skill: a requirement without a
  route is a trap.
- **This is a MAJOR bump** per AGENTS.md — the behaviour of an existing skill changes for everyone.
- **What gets deleted, not adapted:** `references/decompose.md` §PLAN.md and §LEDGER.md,
  `assets/LEDGER.template.md`, `assets/CONVENTIONS.template.md` — the store owns those shapes now.
  Rough estimate from doc 03 §Phase 6: `orchestrate` 333 → ~180 lines.

  > **CORRECTED 2026-09-22.** This bullet used to add *“and most of `references/briefs.md` … its
  > 311 lines mostly evaporate”*, on the strength of doc 03 §4.3's plan to move brief templates to
  > **MCP prompts**. **Prompts were cancelled by measurement on 2026-09-18** — they are invisible
  > to the client (doc 01 §9, [`PROBE.md`](PROBE.md)) — so the destination that bullet assumed does not
  > exist. The stale plan survived here because the correction was made in doc 01 and doc 03 and
  > never chased into this file.
  >
  > And the deeper reason it was wrong: **a brief for a nested session is not bookkeeping.** Its
  > allow-list, its banned-shell-shapes table, its “established facts, do not verify them” block and
  > its named environment noise are the things that stop an agent parking — fourteen launches in
  > the run that built Workman were lost to defects in exactly those sections, and **not one** would have
  > been prevented by anything the store holds. `work_brief` renders an *item's* state; it knows
  > nothing about which verbs a session may run. **`briefs.md` mostly stays.**
- **What stays, because the store cannot hold judgement:** the cost table, the risk dial, red-checks,
  verification doctrine, the phase gate's question, escalation triggers, the critical-thinking
  ceiling.

**Sequencing:** this is doc 03's Phase 6 and it was always gated on the server working. It now does,
measurably — so it is unblocked, and it is a separate undertaking, not a tail on this one.

---

## 4. The always-accessible frontend — one process, two audiences

The owner's standard .NET service blueprint already prescribes the shape, and the design has followed it since
doc 03: **the SPA is built into `wwwroot` and served by the same ASP.NET host.**

```
        ┌──────────────── Workman.Api (one process, always on) ─────────────┐
agents →│  /mcp      Streamable HTTP · OAuth or minted wss_ · role profiles   │
human  →│  /         SolidJS SPA from wwwroot                                 │
        │  /api/*    App API · cookie session · excluded from the OpenAPI doc │
        │  /api/stream  SSE ← Postgres LISTEN/NOTIFY on event.seq             │
        └─────────────────────────────────────────────────────────────────────┘
                                    ↓
                        Postgres (the only state)
```

**Two auth schemes that never cross** is non-negotiable #2 of that blueprint, and it is the reason one
process can serve both safely: a bearer token must never satisfy the dashboard, and a cookie must
never reach `/mcp`.

**The read API is the one shared door (doc 10 rule 5).** `GET /api/v1/*` and `/api/stream` are
read-only, so they take either audience: the dashboard cookie, a minted `wss_` token, or an OAuth
token for the resource `<origin>/api` with the scope `workman.read`. Besides them, `/api/session`,
`/api/agent-sessions` (doc 14 §2, bearer only), the dashboard's writes
(`POST /api/projects/{id}/archive|unarchive` and the rest in doc 10 "Outside v1", cookie only, with
the `X-Workman-Write` header), the admin API `/api/admin/*` (cookie only) and the invite link
`/api/invite/{token}` (anonymous, below) are the only `/api` routes.

### Every credential, and where it lives

Every credential belongs to a user: the OAuth user, the user whose credential minted the `wss_`
session, or the dashboard user.

| Credential | Held by | Obtained | Reaches |
|---|---|---|---|
| **OAuth** access + refresh token | a person's Claude Code session (hand-started or not launched by `agent_launch`), the agents server's grant (`agent_signin()`, kept in `~/.workman/grants.json`), a desktop client | `/mcp` → Authenticate: dynamic registration or a client-ID metadata document, PKCE S256, sign-in and consent through the dashboard cookie. Scopes `workman.read`, `workman.sessions`, `offline_access` | `/mcp` as controller; `GET /api/v1/*` and `/api/stream` with `workman.read`; `POST /api/agent-sessions` with `workman.sessions` |
| minted **`wss_…`** (per session) | an agent `agent_launch` started | `POST /api/agent-sessions` by an OAuth caller with `workman.sessions` or a `wss_` session of the orchestrator or controller role; returned once, the store keeps only its SHA-256. Expires after `ttlHours` (default 72, max 720) **unused** — an idle window each use renews, no hard cap — revoked by `DELETE` | `/mcp` by its granted role (never above its minter's), `GET /api/v1/*` and `/api/stream`; orchestrator and controller also mint. Its calls are that session's: `X-Workman-Agent` is ignored |
| **dashboard cookie** | a person in a browser | `POST /api/session` with a user's name and password | the dashboard and its `/api/*`, the read API, and `/connect/authorize` (OAuth sign-in) |

An OAuth caller names its session with the `X-Workman-Agent` header or `work_session`. A request that
ends 401 logs one line, `Workman.Auth.Challenge` — method, path, `X-Workman-Client`,
`X-Workman-Agent`, `User-Agent`, whether an `Authorization` header was present and why each scheme
refused it — and never the credential's value.

**A local install's `.env`** (`~/.workman/.env`, or `server/.env` in a clone; `server/.env.example`
lists it) holds no token. It holds the dashboard's bootstrap credential
(`WORKMAN_DASHBOARD_USERNAME` / `_PASSWORD`), the Postgres database, user and password, and the
settings below: `WORKMAN_PUBLIC_URL`, `WORKMAN_PUBLIC_URLS`, `WORKMAN_TRUSTED_PROXIES`,
`WORKMAN_API_PORT` (5290) and `WORKMAN_BIND` (`127.0.0.1`). `/workman:setup` writes it with random
passwords and never prints them.

A desktop client signs in the same way: it registers by DCR, signs in with PKCE for the scopes
`workman.read workman.sessions offline_access`, and mints a `wss_` session per agent it launches.

**The dashboard's users (m48).** The cookie is issued to a row of `app_user` (doc 02 §12a), not to the
configured credential. `Workman:Dashboard:{Username,Password}` (`WORKMAN_DASHBOARD_USERNAME` /
`_PASSWORD`) is a **bootstrap credential**: the first `POST /api/session` that presents it while
`app_user` is empty creates the first user — named after the configured username, the presented
password stored only as an ASP.NET Core `PasswordHasher` hash, system administrator, admin of every
workspace that exists then — on an empty database a new `default` one, so they have a tenant to sign in
to. Two first sign-ins at once create one user (`LOCK TABLE app_user`). From
then on only the table decides: **changing or removing the configured password changes nothing**, and
the dashboard keeps working with neither set. A wrong password, an unknown name and a disabled user
get the same 401 and each runs one hash verification. Every cookie request re-reads its user
(the `sub` claim): a user deleted or disabled (`disabled_at`) is signed out on their next request,
and so is a cookie whose security stamp (`workman:stamp`) is no longer the user's
`app_user.security_stamp`, which a password change or reset draws anew (m52). The
configured credential has nothing to do with OAuth (m51): `/connect/authorize` is gated by a
signed-in cookie, and a fresh install with no user has none to present.
Users are managed on the dashboard's Admin page and change their own password on its Account page
(m52, below).

**Tenants (m50).** A tenant is a `workspace` row, and every request acts in exactly one: its
credential's, never one a header, a query parameter or a server default names (C62). Every
authentication path puts it on the principal as the claim `workman:tenant`:

- **Dashboard cookie** — at sign-in, the user's first membership (oldest grant first); a system
  administrator who is a member of nothing lands in the oldest workspace; a user who is a member of
  nothing cannot sign in (403). `GET /api/session/tenants` lists the workspaces the user may enter and
  `POST /api/session/tenant {id}` re-issues the cookie in another, if they are a member of it or a
  system administrator (doc 10). The sidebar's workspace switcher (doc 13) calls it, shown only to a
  user who may enter more than one.
  Every cookie request re-checks: a user whose membership of the cookie's tenant is removed is signed
  out there on their next request.
- **`wss_` token** — its session's workspace. A mint creates the session in the minting credential's
  own tenant (doc 11). A session a user's credential minted works only while that user may enter its
  workspace: removing the membership or disabling the user fails the token on its next use (401).
- **OAuth access token** — the token's `workman:tenant` claim (m51). A token belongs to **one user
  in one tenant**: its `sub` is the user's `app_user` id, and its tenant is the one the user's cookie
  was in when they approved. The consent page names that workspace and offers the user's others to
  switch to first; a user in several tenants signs in once per tenant, and each client's connection
  acts in one. An approval is remembered per user, client **and tenant** (the OpenIddict permanent
  authorization keeps the tenant in its `workman_tenant` property): approved in A, the same client
  authorized from B asks again and gets a token for B. Every call and every refresh re-checks that
  the user exists, is not disabled and may still enter the tenant; otherwise the call is refused
  403 and the refresh `invalid_grant`, each saying to sign in again. The role is controller for a
  member, an admin and a system administrator alike: tenant roles decide who manages members, not
  what the work tools may do.
  **Refresh tokens are opaque reference handles (m57)**: 43 characters naming a row of
  `openiddict_token`, which holds the encrypted payload; access tokens stay self-contained. Why: a
  Windows client keeps its refresh token in Credential Manager, whose blob (`CredWriteW`) takes at
  most 2560 bytes, and a self-contained refresh token — an encrypted JWT carrying the user, the
  tenant and the authorization — measures 2875 characters. Rotation, the 30-second reuse leeway for
  concurrent refreshes and revocation — of the token or of its authorization — work on the handle.

A principal with no tenant — including a request carrying two credentials that name different
workspaces — gets `403 {"error": "…"}` saying what to do. No request creates its own tenant:
workspaces are created only by the first user's bootstrap (`default`, when there is none) and by a
system administrator on the Admin page (`POST /api/admin/tenants`). A tree lock is
per tenant too: a path claimed in one workspace does not hold the same path in another (doc 07 §5).
Within one tenant a path is also per **user** (m60): it names a folder on one person's machine, so a
path lock and a repository's `root` binding are the signed-in user's own, and two users' same path are
two checkouts. A remote, a branch and the ref scope name the same thing for everyone and stay the
tenant's.

**Administration (m52).** Roles govern administration only: every member of a workspace has full
work rights there, whatever its role. A **system administrator** (`app_user.is_system_admin`) sees
and manages every workspace, creates workspaces and lists, disables and re-enables users; a **tenant
admin** (`tenant_membership.role = 'admin'`) invites into their own workspace and changes or removes
its members; a **member** reaches no admin route (403). The admin API (doc 10 "Outside v1") names the
workspace it acts on in its path — administering a tenant is not acting in it, and a system
administrator administers all of them — so every call is judged against the caller's rights in that
workspace **as the table has them on that request**, never against the cookie's tenant or anything
the request says about its caller (C62): a tenant admin of A is refused (403) every read and write
aimed at B, and a workspace's id is checked before its existence, so they learn nothing about B. A
workspace keeps at least one admin: demoting or removing its last one is refused (409), under a lock
on the workspace row so two admins demoting each other at once cannot both succeed. A system
administrator cannot disable themselves. Disabling a user ends their cookies, their OAuth tokens and
the sessions they minted at the next request each makes; enabling them undoes it.

**Invite links (m52).** No email is sent: an admin creates a link and copies it to the person. It is
`<origin>/join/<token>`, the token 32 random bytes in base64url, **shown once** in the create
response; `user_link` keeps only its SHA-256 (C64), so a lost link is revoked and made again, never
shown again. An invite names a workspace and a role, is valid **14 days** and works **once**: it is
used by one `UPDATE user_link SET used_at = now() WHERE token_hash = … AND used_at IS NULL AND
expires_at > now() RETURNING …`, so of two requests racing with one token exactly one gets a row.
Accepting it with no session creates the user — a name unique ignoring case and a password of **at
least 12 characters** (and at most 1024), stored hashed — with that membership; with a signed-in
dashboard cookie it gives that user the membership instead (never lowering a role they already hold
there). Either way the browser is then signed in to the invite's workspace. A name already taken is
refused (409) and leaves the link unused. `GET /api/invite/{token}` and `POST
/api/invite/{token}/accept` take no session and are rate-limited per client address (10 a minute,
sign-in's window doubled because the page reads the link before it uses it); the accept also needs
the `X-Workman-Write` header. A used, revoked, expired or unknown token is one answer, 404.

**Passwords (m52).** A password is at least **12 characters** (and at most 1024) wherever one is
set: accepting an invite, changing one's own, using a reset link. `POST /api/session/password
{current, next}` (the cookie and the `X-Workman-Write` header) changes the signed-in user's
password; a wrong current password is sign-in's bare 401, and it is rate-limited like sign-in, in a
window of its own. Success rehashes and draws a new security stamp, which **ends every other cookie
of that user** at its next request, and re-issues the requesting cookie with the new stamp, so it
stays. **OAuth grants and minted `wss_` sessions stay**: their tokens are not cookies and carry no
stamp. A **reset link** is the invite's mechanism for one user: `POST
/api/admin/users/{id}/reset-link` returns `<origin>/reset/<token>` once (only the SHA-256 kept, C64),
valid **24 hours**, used once by the same conditional `UPDATE`; a newer link for the same user
replaces an unused older one. Using it (`POST /api/reset/{token} {password}`, no session needed,
rate-limited like an invite) sets the password and draws a new stamp, which **ends all of the
user's cookies**, the browser's own included; OAuth grants stay. A reset hands its holder the whole
account, so a system administrator makes one for anyone, and a tenant admin only for a user who is
no system administrator and every one of whose workspaces they administer — never for someone with
a membership in a workspace that is not theirs (C63); every refusal, an unknown id included, is the
same 403.

**Projects: private by default, shared by members (m58).** A project has a member list
(`project_member`, doc 02 §12b) — the people who may work in it. Every member has full read and
write; there is no read-only member. A project is **private to its creator** until shared: the user
whose OAuth token or minted `wss_` session ran `work_create` is its one member. The offline importer
has no user to name, so a project it creates gets **every current member of the workspace**. A user
who is not a member of the workspace they act in (a system administrator visiting it) cannot create a
project there (m59): only a workspace's members can be project members, so it would be hidden from its
creator.
Sharing is the dashboard's: `GET|POST /api/projects/{id}/members`, `DELETE
/api/projects/{id}/members/{user}` (doc 10 "Outside v1"), allowed to a member of the project and to
an admin of its workspace (a system administrator is one of every workspace). Anyone else — a
member of the workspace who is not in the project, a user of another workspace — gets the 404 an
unknown id gets, so a private project's existence is not revealed. **Add everyone** (`{"all":
true}`) is how a project becomes tenant-wide; a member who joins the workspace later is not added
to its projects. A project keeps at least one member: removing its last is refused (409), and so is
removing that user from the workspace, naming the projects, until someone else is in each.
Otherwise leaving a workspace ends its project memberships with it; **disabling a user does not**
(they may be enabled again).

**Only a project's members reach it (m59, doc 02 §15b).** Membership decides every read and write, in
the database: a member of the workspace who is not in a project finds none of it — not in the overview,
the inbox, a search, the journal, the live stream, a `/r/` link or a ref that names it — and is answered
as for a ref or an id that does not exist. A default scope (`work_brief()`, `work_next()`) lands only in
a project the caller is in. There is no read-only member and no read-only share. A workspace admin
manages any project's members without being one, and reads no project they are not in. A project no one
has joined yet is every member's until its first member joins.

**`WORKMAN_PUBLIC_URL`** (`Workman:PublicUrl`, **optional**; compose passes
`${WORKMAN_PUBLIC_URL:-}`) is the base of the `web` line `work_brief` and `work_get` print
(doc 01 §8). Unset, the line names the scheme and host the MCP request came in on — from the host
that is `http://127.0.0.1:5290`, which is also where the dashboard is. Set it when humans reach the
dashboard at another address than agents reach `/mcp` (a reverse proxy, a LAN name). An `https`
public URL also makes the dashboard cookie `Secure` unconditionally; otherwise the cookie follows the
request's scheme.

**`WORKMAN_TRUSTED_PROXIES`** (`Workman:TrustedProxies`, **optional**; compose passes
`${WORKMAN_TRUSTED_PROXIES:-}`) is a comma-separated list of IPs or CIDRs whose `X-Forwarded-Proto`,
`X-Forwarded-Host` and `X-Forwarded-For` the API believes. Set it to the reverse proxy's address (on
a docker network, that network's CIDR). From those addresses the request's scheme and host become
the forwarded ones before authentication runs, so the `web` line (when no public URL is set), the
`/r` redirects and the cookie's `Secure` flag all follow the `https` origin the proxy is reached on.
From any other address the headers are ignored. Empty, the default, trusts nobody and leaves the
forwarded-headers middleware out of the pipeline: a local install has no proxy, and a header any
client can send must not choose the origin. A malformed entry stops the API at startup.

These and `WORKMAN_PUBLIC_URLS` are the only settings that differ between a local and a server
install (rule C32): the image, the port it listens on (8080), the credentials and the code path are
the same. The reverse proxy itself (TLS, Traefik) is not part of this repository.

### How to make it always-on

**Both composes have `restart: unless-stopped` on both services.** So, from a clone:

```
docker compose -f server/compose.yml up -d
```

…or, with no clone at all, `/workman:setup` (its script) writes `~/.workman/compose.yml`,
which **pulls the published image** instead of building one, and runs the same command against it.

> **The published image.** `.github/workflows/image.yml` builds `server/Dockerfile` for
> `linux/amd64` and `linux/arm64` when a tag `v<version>` is pushed. The tag must equal
> `ServerInfo.Version`, and a version already on ghcr is refused rather than rebuilt. It pushes
> `ghcr.io/getsupraman/workman:<ServerInfo.Version>` and `:latest`, labelled
> `org.opencontainers.image.source=https://github.com/getsupraman/workman`. The pull-only compose is
> `plugin/skills/setup/assets/compose.yml`: the same services, volume and project name (`workman`) as
> `server/compose.yml`, so a machine can move between them and keep its data. `server/compose.yml`
> stays the build-from-source compose for development. The Dockerfile's build stages run on the
> builder's platform and cross-target (`-a $TARGETARCH`): `dotnet restore` under QEMU aborts with
> exit 134.

…and the UI is at `http://127.0.0.1:5290` permanently, surviving reboots wherever Docker Desktop
starts at logon. Agents reach `/mcp` on the same host and port. **Nothing needs to know an agent is
working** — the SSE stream is fed by `LISTEN/NOTIFY` on the event journal, so the board updates
because the *database* changed, not because a session is open.

That is the payoff of the event table being append-only with a monotonic cursor: the human's view
and the agent's view are the same data, and neither depends on the other being present.

**Without Docker:** publish the API and run it as a Windows service (`sc.exe create`) or a scheduled
task at logon. Same result, more moving parts.

### Two things the always-on model brings that are worth deciding now

- **It becomes a single point of failure and a single backup target.** Every project's plan, ledger
  and evidence lives in one Postgres volume. **Back that volume up**, or the thing that replaced 181
  markdown files in git becomes the one artifact that is not in git. `setup.sh --backup <file>` /
  `--restore <file>` run `pg_dump -Fc` / `pg_restore --clean` inside the `db` container; a restore
  writes a safety dump first. Nothing runs them on a schedule.
- **Bind to localhost.** `WORKMAN_BIND` defaults to `127.0.0.1`, and `compose.dev.yml` publishes
  Postgres on `127.0.0.1:55433`. Keep it that way until there is a reason not to: locally the
  dashboard's cookie and the OAuth sign-in travel over plain HTTP.
