# Install and self-hosting

Workman is two things: a **server** your agents talk to over MCP (with the dashboard on the same
port), and the **`workman` plugin**, which gives Claude Code the `/workman:*` skills. You connect to a
server once, install the plugin once, and every session on that machine has both.

## The hosted server

[work.supraman.ai](https://work.supraman.ai) runs Workman for you (access by invitation today). Install
the plugin (below), then connect Claude Code to it once, at user scope so every project sees it:

```
/workman:setup --url https://work.supraman.ai
```

It touches no Docker and writes no `.env`: it copies the session helper to `~/.workman/`, proves the
server asks for sign-in (`POST /mcp` without a token answers `401` with a `resource_metadata=` link
that answers), and registers `https://work.supraman.ai/mcp` with browser sign-in. Restart Claude Code,
run `/mcp`, pick `workman` and **Authenticate**; there is no token to paste or store. Do the same on
each machine you work from: they all write to the same record.

An existing `workman` registration that points at another server is kept; `--force` re-points it,
which is how you move from one server to another. Without the plugin, the same registration by hand
(no session helper, no proof) is
`claude mcp add --transport http --scope user workman https://work.supraman.ai/mcp`.

## The plugin

In Claude Code:

```
/plugin marketplace add getsupraman/workman
/plugin install workman@supraman
```

Restart Claude Code. The `mcp__workman__*` tools come from the server you registered; the skills
(`/workman:plan`, `/workman:orchestrate`, `/workman:controller`, `/workman:agents`,
`/workman:setup`, `/workman:import`) come from the plugin. A plugin update arrives when its version
changes; `/plugin` shows the installed one.

## Self-hosting with `/workman:setup`

Install the plugin, then run `/workman:setup`. A machine needs **Docker and nothing else**.

The skill finds which state the machine is in and runs one shipped script
([`setup.sh`](../plugin/skills/setup/assets/setup.sh) or
[`setup.ps1`](../plugin/skills/setup/assets/setup.ps1)) that does only the part that is missing:

1. writes a local `.env` with random tokens and a dashboard password, in `~/.workman/`;
2. pulls the published image `ghcr.io/getsupraman/workman` and starts it with its Postgres 17;
3. registers `http://127.0.0.1:5290/mcp` with Claude Code as `workman`, with browser sign-in by default;
4. proves the server answers the way that registration needs.

The model never reads the `.env` and never sees a token. The dashboard is at
`http://127.0.0.1:5290`; its password is in the `.env` the script names. Restart Claude Code once the
script is done: MCP servers are resolved at session start.

The same skill keeps the install running:

| Ask for | What it does |
|---|---|
| *"Update Workman"* (`--update`) | pulls the image again, or rebuilds it from a clone |
| *"Back up my Workman database"* (`--backup <file>`) | dumps the database to a file |
| *"Restore Workman from &lt;file&gt;"* (`--restore <file>`) | writes a safety dump first, then replaces the data |
| a clone of this repository (`--clone <path>`) | builds the image from your clone instead of pulling it |

Every flag and status line is in [plugin/skills/setup/SKILL.md](../plugin/skills/setup/SKILL.md).

## One server for a team

To share one server between machines and people, run it where they can all reach it, behind a reverse
proxy with HTTPS, and register it on every machine the way the hosted server is registered above:
`/workman:setup --url https://<your origin>`. Each user signs in with their own account; the dashboard's Admin page creates
workspaces and invite links.

Two settings differ from a local install:

- **`WORKMAN_PUBLIC_URL`**: the origin people and agents reach the server on, for example
  `https://workman.example.com`. It is the base of the links `work_brief` and `work_get` print.
- **`WORKMAN_TRUSTED_PROXIES`**: the address or CIDR of the reverse proxy, so the server believes its
  `X-Forwarded-*` headers. Empty trusts nobody.

[server/deploy/multi-deploy/](../server/deploy/multi-deploy/README.md) is a complete example: a
compose file, secret generation, backups on a systemd timer, upgrades and restore.

### Several public URLs

One server can answer on several host names. List the extra origins, comma-separated, in
**`WORKMAN_PUBLIC_URLS`** (setting `Workman:PublicUrls`), after `WORKMAN_PUBLIC_URL`:

```bash
WORKMAN_PUBLIC_URL=https://workman.example.com
WORKMAN_PUBLIC_URLS=https://workman.example.org
```

Each origin is its own OAuth issuer. A request is answered as the origin its `Host` names, and a host
that names none as the first. A sign-in at one origin works at that origin; adding another origin does
not disturb tokens already issued. An entry that is not an absolute `http(s)` URL stops the server from
starting. Route every host to the server in your reverse proxy (the multi-deploy example uses
`WORKMAN_HOSTS` for its Traefik rule).

## Running from source

To develop Workman itself, build and test the server, the dashboard and the plugin as
[CONTRIBUTING.md](../CONTRIBUTING.md) describes, and see
[server/frontend/README.md](../server/frontend/README.md) for the dashboard's dev server.

## Further reading

- [08-distribution.md](08-distribution.md): why the server runs on its own, every setting and every
  credential, in full.
- [12-install-harness-identity.md](12-install-harness-identity.md): what Claude Code's MCP support
  does on a fresh machine, and where configuration lives.
