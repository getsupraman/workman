# Security policy

## Reporting a vulnerability

**Do not open a public issue, discussion or pull request for a vulnerability.**

Report it privately through GitHub's private vulnerability reporting: on
[getsupraman/workman](https://github.com/getsupraman/workman), open **Security** →
**Report a vulnerability**, or go straight to
[the advisory form](https://github.com/getsupraman/workman/security/advisories/new). Only the
maintainers see the report.

Please include what is affected (server, dashboard or plugin, and the version), the steps that
reproduce it, and what an attacker gains. A maintainer acknowledges the report, keeps you informed in
the advisory, and credits you when the fix is published unless you prefer not to be named.

## Scope

In scope:

- **The Workman server** (`server/`, the `ghcr.io/getsupraman/workman` image): the MCP endpoint, the
  read API, OAuth sign-in and tokens, role and tenant separation, the dashboard's session handling.
- **The dashboard** (`server/frontend/`).
- **The `workman` plugin** (`plugin/`): the skills, the agents MCP server, and the setup scripts,
  including anything that could leak a token or run a command the user did not ask for.
- **The hosted service at [work.supraman.ai](https://work.supraman.ai)**, for flaws in Workman itself.
  Do not test against other users' data, and do not run denial-of-service or automated scanning
  against it.

Out of scope: vulnerabilities in Claude Code, Docker, Postgres or other third-party software (report
those upstream), and findings that need an already-compromised machine or a self-hosted server
configured against its documentation.

## Supported versions

Fixes are made to the latest release of the server image and of the plugin. Upgrade to the latest of
both before reporting.
