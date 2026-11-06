---
name: setup
description: "Get the Workman MCP server running and registered, so the `work_*` tools are callable. Workman is a self-hosted HTTP MCP server (a .NET service plus Postgres, in Docker) with an operator dashboard on the same port. One shipped script does only the part that is missing: writes the local `.env` with random passwords, pulls the published image or builds from a clone, starts it, registers it with OAuth sign-in (no token stored), writes `~/.workman/server.json`, and proves the server asks for that sign-in. With `--url` it registers a remote Workman server instead, such as the hosted https://work.supraman.ai, with no Docker at all. Also backs up and restores the database. USE FOR: `work_brief` or any other `work_*` tool is missing and a skill told you it needs the store; a first install of Workman; connecting to a hosted or team Workman server, or moving to another one; \"the workman tools are gone\"; the server is up but this session cannot see it; backing up or restoring Workman. DO NOT USE FOR: using the tools once they work (call `work_brief`), or deploying the Workman server anywhere but localhost."
---

# Workman setup

**After running this, `work_brief` works — in the *next* session, not this one.** MCP servers are
resolved at session start; nothing you do here can add a tool to the session you are standing in.
Say that up front, or your own success reads as a failure.

Workman is a server **you** run. Claude Code only ever connects to it. Everything below is one
script, shipped next to this file — `assets/setup.sh` (bash: macOS, Linux, WSL, and Git Bash on
Windows, which is what the Bash tool runs) — and your job is to pick its flags and read its status
lines. Find it with `echo "$CLAUDE_PLUGIN_ROOT"`: the script is at
`$CLAUDE_PLUGIN_ROOT/skills/setup/assets/setup.sh`.

## What the model never does

The passwords live in one file, the install's `.env`. The script hands them to Docker itself; **you
never see one, and you keep it that way:**

- **never `Read`, `cat`, `grep` or open the `.env`**, and never write one yourself;
- **never run `claude mcp get` or `claude mcp list` to check Workman**, nor start a `claude` process to
  probe it: a token-less check blocks every agent's Workman tools for 15 minutes. `--dry-run` is the check;
- never put a token on a command line, in a `curl`, or in a file you write.

The dashboard password is for the human: tell them it is in the `.env` the script names; do not print
it. Every line the script prints is safe to read — it was written and tested so that no value from
the `.env` appears in its output.

---

## 1. Which state is this machine in? Say which, before running anything

**First question: can you call `work_brief` right now?** If `work_brief` **and** `work_constrain` are
in your tool list, this is the **already working** state. Say so, change nothing, and stop — unless
`work_brief` says *"session unnamed — /workman:setup installs the session helper"*: then
run the script with `--force` (§3), which re-registers with the helper, and nothing else.

`work_brief` there but **no `work_constrain`** is a fifth state: **registered and running, but older
than the skills.** Run the script with `--update` (§3); registration is left as it is.

**If the user named a server to connect to** (an `https://` URL, such as the hosted
`https://work.supraman.ai`), that is §2a: skip the checks below.

Otherwise run the script's dry run in the **Bash tool**. It writes nothing, starts nothing and
registers nothing; it reads the existing registration's url from Claude Code's config file and asks
`/health`:

```
bash "$CLAUDE_PLUGIN_ROOT/skills/setup/assets/setup.sh" --dry-run
```

| `register` line | `start` line | State | Do |
|---|---|---|---|
| `dry run, would run …` | `/health answered` not `200` | **nothing present** | §2, then §3 |
| `kept … of http://127.0.0.1:…` | `/health answered` not `200` | **registered, server down** | §2, then §3 — the script keeps the registration |
| `dry run, would run …` | `/health 200` | **server up, not registered** | §3 — it finds the `.env` already there and only registers |
| `kept … of http://127.0.0.1:…` | `/health 200` | **registered and healthy, this session just started too early** | §5 only — restart, and change nothing else |
| `kept … which points at https://…` | — | **a remote registration** | nothing runs here, it is §5; failing, re-run §2a with that origin to prove it; another server wanted, §2a with `--force` |

The fourth row is the one worth reading twice: a registered, healthy server with no `work_*` tool in
your list is not broken, it is a session that began before the registration did, or an OAuth
registration nobody has signed in to yet. Change nothing; the user signs in (§5).

---

## 2a. Hosted: a remote server

**If the user names a server** — the hosted `https://work.supraman.ai`, or a team's own Workman behind
HTTPS — nothing runs on this machine and there is no Docker, no `.env` and no token. Pass its origin:

```
bash "$CLAUDE_PLUGIN_ROOT/skills/setup/assets/setup.sh" --url https://work.supraman.ai
```

The script copies the session helper to `~/.workman/`, proves the server asks for sign-in (`POST /mcp`
without a token answers `401` with a `resource_metadata=` link that answers `200`) **before** it
registers anything, registers `<origin>/mcp` at user scope with OAuth and the helper, writes
`~/.workman/server.json`, and runs the shadow check. A URL that fails the proof is never registered: the `prove` line says what it answered.

An existing `workman` registration that points at **another** URL is kept, and the `register` and
`next` lines say where it points; `--force` re-points it to the new one. Moving from one server to
another is exactly that, and it is the user's call: ask before you pass `--force`.

`--url` takes an origin (`https://host[:port]`, no path; plain `http` only for `127.0.0.1`). It refuses
the local-install flags (`--clone`, `--port`, `--image-version`, `--update`, `--backup`,
`--restore`): a remote server is signed in to in the browser with the user's own account. `--force`
and `--dry-run` work as below. Then §5.

```
helper    /home/u/.workman/session-header.mjs (names each session)
auth      oauth: --url: a remote server is signed in to with OAuth, no token stored
prove     POST /mcp without a token: 401 with resource_metadata=https://work.supraman.ai/.well-known/oauth-protected-resource/mcp, which answers 200 - Claude Code finds the sign-in from there
register  'workman' -> https://work.supraman.ai/mcp with OAuth sign-in, session helper (user scope; no token stored)
server    /home/u/.workman/server.json -> https://work.supraman.ai
shadows   none: no local or project 'workman' entry overrides it
dashboard https://work.supraman.ai - sign in with your account there
next      start a new Claude Code session, run /mcp, pick 'workman', Authenticate, sign in in the browser and Approve: the work_* tools appear there, not in this one
```

## 2. Self-hosting: the Workman home, or a clone

**The default needs no clone.** The script keeps everything in the **Workman home**,
`~/.workman/` (`%USERPROFILE%\.workman` on Windows): a `compose.yml` that pulls the published
image `ghcr.io/getsupraman/workman`, and the `.env`. A machine needs Docker and nothing else.

Use **`--clone <path>`** instead when the machine already runs Workman from a clone of
`github.com/getsupraman/workman` (a `<clone>/server/.env` exists), or the user develops Workman: the script
then uses `<clone>/server/compose.yml`, which builds the image from source, and `<clone>/server/.env`.

**An existing clone install keeps `--clone`.** Both composes use the project name `workman` and so
the same database volume — but the volume only accepts the Postgres password it was created with,
which is in the clone's `.env`. Moving such a machine to the home means the user copies
`<clone>/server/.env` to `~/.workman/.env` first (a `cp`; nobody needs to read it). The script
refuses to write a new `.env` next to an existing database volume, so a wrong guess stops with
`env  … already has a database volume` instead of locking the API out of its data.

Never clone anything yourself: the home install does not need it, and a clone is a directory on
someone else's machine that they choose.

---

## 3. Run the script

From the project directory, in the Bash tool:

```
bash "$CLAUDE_PLUGIN_ROOT/skills/setup/assets/setup.sh"
```

| Flag | Does |
|---|---|
| `--url <origin>` | register a remote server instead of a local one (§2a) |
| `--clone <path>` | use a clone (§2) instead of the home |
| `--port <port>` | host port, used only when it writes a **new** `.env` (default 5290) |
| `--image-version <tag>` | pin the image instead of `latest` |
| `--update` | pull the image again (home) or rebuild it (clone) |
| `--force` | replace the existing `workman` registration (with `--url`: re-point it) |
| `--dry-run` | write nothing, start nothing, register nothing; prove what is up and show the registration |
| `--backup <file>` / `--restore <file>` | §4, and nothing else |

**OAuth is the only sign-in.** The script registers `workman` at user scope with **no
`Authorization` header** and stores no token anywhere. On first use the user signs in once in
the browser: in a new session, `/mcp`, pick the server, **Authenticate**, sign in with the
**dashboard user** (`WORKMAN_DASHBOARD_USERNAME` / `_PASSWORD` from the `.env`) and **Approve**.
The server remembers the approval, so a later sign-in skips the consent page. A server image older
than OAuth (an old pull of `latest`) does not offer it: the `prove` line stops and names `--update`.
An existing registration is kept as it is unless you pass `--force`.

Every registration also writes `~/.workman/server.json` — `{"url": "<origin>", "mcpName": "workman"}`,
atomically — which is how the rest of Workman finds the server; `--dry-run` writes nothing.

**Register `127.0.0.1`, never `localhost`.** The server binds IPv4 loopback only, and the sign-in is
bound to the host it was reached on. The script always writes `http://127.0.0.1:<port>/mcp`; do not
edit it into `localhost`.

The registration also runs `session-header.mjs` (copied to `~/.workman/`; the script needs `node`) on
each connect: it keys the session by its Claude Code process, so `work_session(name)` can name a
hand-started session that shares this registration. An older registration without it needs `--force`
once, then a new session. Sessions launched by `/workman:agents`'s `agent_launch` get their own minted
token and name instead; sign it in once with `agent_signin`.

Each step prints one line; every step is idempotent, so running it again is always safe:

```
home      /home/u/.workman (pull-only compose written)
env       wrote /home/u/.workman/.env: dashboard and Postgres passwords (values not shown, mode 600)
helper    /home/u/.workman/session-header.mjs (names each session)
start     project 'workman' up, http://127.0.0.1:5290/health 200
auth      oauth: sign in as the dashboard user, no token stored
prove     POST /mcp without a token: 401 with resource_metadata=http://127.0.0.1:5290/.well-known/oauth-protected-resource/mcp, which answers 200 - Claude Code finds the sign-in from there
register  'workman' -> http://127.0.0.1:5290/mcp with OAuth sign-in, session helper (user scope; no token stored)
server    /home/u/.workman/server.json -> http://127.0.0.1:5290
shadows   none: no local or project 'workman' entry overrides it
dashboard http://127.0.0.1:5290 - sign in with WORKMAN_DASHBOARD_USERNAME / _PASSWORD from /home/u/.workman/.env
next      start a new Claude Code session, run /mcp, pick 'workman', Authenticate, sign in in the browser and Approve: the work_* tools appear there, not in this one
```

An existing `.env` is **never overwritten** (`env  kept …`).

**Report the `prove` line to the user.** There is no token to list tools with, so it proves what
Claude Code needs to find the sign-in: `POST /mcp` without a token answers `401` with a
`resource_metadata=` link, and that document answers. It runs before anything is registered; a
failing proof registers nothing and its line names the `docker compose … logs api` to look at, or
`--update` for an image older than OAuth. The tools themselves show up only after the sign-in in the
next session (§5).

`shadows` lists every other entry of the same name that wins over the one just registered — a
local-scope entry in `~/.claude.json` for some project, or a project-scope one in this directory's
`.mcp.json` (Claude Code resolves local > project > user). Sessions in that directory then use the
old entry: no session helper, often an old token, while `register` and `prove` look fine. Each line
names the directory, url and whether it has a helper, never a header value, and ends with the
command that removes it. The script removes nothing: **show the user the lines and let them run the
command in that directory**; a project entry is a committed team file, so it is theirs to agree with
the repo, not yours to edit. `not checked` means the config was unreadable.

When it stops instead, the last line says why. `docker  not reachable` means Docker itself is not
running: **ask the user to start Docker Desktop** (Linux: `sudo systemctl start docker`; macOS with
Colima or OrbStack: `colima start` or open OrbStack) and run it again — that is not something to
work around. `… is NOT gitignored` (clone only) means one `git add` would publish the passwords: stop
and tell the user, and do not edit `.gitignore` yourself.

The image is multi-arch (amd64 and arm64), so Apple Silicon needs nothing special, and
`restart: unless-stopped` brings the stack back wherever Docker starts at logon — you run this once,
not once per session.

---

## 4. Back up and restore

The Postgres volume is the only copy of every plan, claim and verification. Back it up before
anything risky (an upgrade, a restore, moving machines):

```
bash "$CLAUDE_PLUGIN_ROOT/skills/setup/assets/setup.sh" --backup ~/workman-2026-10-26.dump
```

It runs `pg_dump -Fc` inside the running `db` container (found by compose project `workman`) and
copies the dump out; no password passes anywhere. `--restore <file>` first writes a
safety dump of the current database to `<home>/backups/`, stops the API, runs
`pg_restore --clean --if-exists`, and starts the API again — **it replaces the data**, so confirm
with the user before you run it. Pass `--clone <path>` for a clone install, as everywhere else.

---

## 5. Restart — and say so

You cannot finish this yourself — the last step is the user's. **Tell them, in plain words, that the
`work_*` tools are registered and will appear in their next session, not this one.** Ask them to:

1. Exit this session and start a new one in the same project.
2. Run `/mcp`, pick the server, choose
   **Authenticate**, sign in in the browser with the dashboard user from the `.env`, and
   **Approve**. `/mcp` then says it is connected. Claude Code stores the grant; this is not a step
   for every session.
3. Call `work_brief` — it is the recovery payload and it needs nothing read before it. On an empty
   server it answers with onboarding instead of an error. Projects are bound to repositories, one
   project per repo: `work_brief(repo: {remote, root})` opens the repo's project, and onboarding
   names the `work_create` that binds an unbound one.

If the new session still has no `work_*` tool, the registration did not land: run the script with
`--dry-run` from that project (its `register` and `shadows` lines say what is registered and what
overrides it), never `claude mcp list`.
