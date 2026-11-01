# API token hardening — Phases 1–5

## Goal

Every credential the Acme API issues carries an explicit, verifiable purpose, and every endpoint group
accepts only credentials minted for it. When this is done: a webhook signing key cannot call the
public API; a dashboard session cannot call `/api/admin/*`; the CLI token can no longer choose which
account it acts as, and cannot act as a suspended one; the billing robot is resolved by role rather
than by a name prefix; the export link handed to a browser expires in minutes rather than a week and
dies when the account's password changes; and a plugin's permissions come from signed claims with
real checks behind them rather than from request headers. A human confirms this by running
`dotnet test Acme.Tests/Acme.Tests.csproj` — specifically the credential×endpoint matrix added in
Phase 1, which asserts one allowed cell per credential — and by reading `PublicRouteTests` and
`PluginHandshakeTests` still green, which prove the public surface and the plugin handshake did not
change.

Findings closed: `F0`, `F1`, `F2` (reach only — see constraint 3), `F3`, `F4`, `F6`, `F8`, `B9`,
`C6` (reachability via token).

## Binding constraints from the owner

1. **`X-Acme-Account` goes.** Identity comes from the token. The header has exactly one reader in the
   repo (`AccountResolver.cs`) and no other caller anywhere.
2. **The CLI token stays**, as an operator-only credential. `Acme.Tests/Cli/CliTests.cs` is its only
   holder in this repo. Do not delete the `CliToken` scheme.
3. **The billing robot keeps its service token** in the `/tokens` response. Do **not** filter it out
   of `TokenService.ListTokens`. Phase 1 confines that token to `/api/billing/*`; its one-day
   lifetime is accepted, not fixed.
4. **Partner webhook keys may not be rotated** — rotating requires a partner call. No re-keying, no
   expiry enforcement on `WebhookKey`. Out of scope.
5. **No response-cache regressions.** Do not restructure the list endpoints. The edge cache serves
   71% of list reads over 30 days; that is what a careless change would put at risk.

## Phases

### Phase 1 — Audience separation *(the foundation; everything else is smaller after it)*

Today every token — dashboard login, CLI tokens, export links — comes out of
`TokenIssuer.Create` with issuer `acme` and audience `acme-api`, signed by one key and validated by
one `Bearer` scheme. They are indistinguishable, so every endpoint group naming `Bearer` accepts all
of them. That single fact is `F2`'s reach, `F3`'s reach, `B9`, and `C6`'s reachability.

| Task | Deliverable | Verify |
|---|---|---|
| **T1.1** | `AcmeAudiences` constants (`acme:web` / `acme:cli` / `acme:plugin`). Minting takes an explicit audience; every call site passes one. Register `CliJwt` + `PluginJwt` schemes. **All schemes temporarily accept all three audiences plus the legacy `acme-api`** so the tree stays green and no behaviour changes yet. | `dotnet build Acme.slnx` clean **and** `dotnet test --filter "FullyQualifiedName~Auth\|FullyQualifiedName~Cli"` green — a no-regression task, so green is the criterion |
| **T1.2** | **Test first, then the change.** Write `TokenAudienceTests`: every credential × every endpoint group, one allowed cell per credential row. **Red-check it against the un-repointed code and report the failure count** — it must fail, because today every group accepts every token. Then repoint the groups (default → Cookie + `Bearer`; `cli` → `CliToken` + `CliJwt`; `plugin` → `PluginJwt`) and narrow each scheme to its own audience, `CliJwt` also accepting legacy `acme-api` for one release. | `dotnet test --filter "FullyQualifiedName~TokenAudienceTests"` green, having first been red |

### Phase 2 — The CLI token (`F0`, `F1`, `F8`)

`F0` is a sample CLI token checked into `CliTests.cs`. Revoking it is the owner's task; getting it out
of tracked source is ours.

| Task | Deliverable | Verify |
|---|---|---|
| **T2.1** | The test credential moves out of tracked source (configuration / `.env`, skipped when absent, never silently passing). | `dotnet test --filter "FullyQualifiedName~CliTests"` |
| **T2.2** | Delete the `X-Acme-Account` branch in `AccountResolver.Resolve`. Add `AccountService.IsActive` before sign-in, as the dashboard path already does. | `dotnet test --filter "FullyQualifiedName~Cli"` |

### Phase 3 — The billing robot (`F6`)

`AccountService.GetBillingRobot` finds it with `Name.StartsWith("robot-")` — no ordering, no role
check — so a customer who names a real user `robot-ops` non-deterministically shadows it, and
billing jobs run as that human. `WhereRobot` / `WhereHuman` already resolve by role and are the model.

| Task | Deliverable | Verify |
|---|---|---|
| **T3.1** | `GetBillingRobot` resolves on the `BillingRobot` role, deterministically. | `dotnet test --filter "FullyQualifiedName~Accounts"` |

### Phase 4 — The export link (`F3`)

`ExportController.cs` mints a 7-day link carrying the account's whole principal. Phase 1 already
confines it to `/exports`. Two things remain, neither of which touches the export format.

| Task | Deliverable | Verify |
|---|---|---|
| **T4.1** | Lifetime 7 days → 30 minutes. The download starts within seconds of the click, so 30 minutes is safe under any browser's retry behaviour. | `dotnet test --filter "FullyQualifiedName~Exports"` |
| **T4.2** | Carry the account's password stamp in the link and validate it per request, so a password change / suspension / sign-out-everywhere kills outstanding links. | `dotnet test --filter "FullyQualifiedName~Exports"` |

### Phase 5 — Plugin permissions (`F4`)

Four compounding defects, ordered by what makes the others exploitable.

| Task | Deliverable | Verify |
|---|---|---|
| **T5.1** | Gate `invoke`, not only `list`. Today `PluginScopeAttribute` is consulted only by the listing filter, so a plugin that hard-codes a name invokes actions filtered out of its listing. | `dotnet test --filter "FullyQualifiedName~Plugins"` |
| **T5.2** | `PluginContext.GetScope` reads claims only — no header fallback. `PluginScopeAttribute.IsValid` defaults to **deny**. | `dotnet test --filter "FullyQualifiedName~Plugins"` |
| **T5.3** | Real checks in `ProjectSearch` (account↔project ownership) and `SharedFolderSearch` (`FolderMember` membership). Both check the account only today, despite a doc-comment claiming otherwise. | `dotnet test --filter "FullyQualifiedName~Plugins|FullyQualifiedName~Search"` |

### Phase 6 — Plugins can refresh silently (owner-requested)

Today the token endpoint allows the authorization-code flow only, and plugin registration grants
`authorization_code` with no `offline_access`. A plugin therefore receives a 1-hour access token and
nothing to renew it with: at the hour mark it needs a front-channel trip through `/oauth/authorize`,
which skips the consent screen only while the user's `acme.example` cookie is alive.

This is a feature, but it *tightens* security rather than loosening it. The server re-checks the
grant on every refresh redemption, so the revocation added in `a1b2c3d4e` starts biting at the next
refresh instead of coasting to the hour — right now that revocation is blunted by there being no
refresh to intercept.

| Task | Deliverable | Verify |
|---|---|---|
| **T6.1** | Refresh-token flow enabled, `offline_access` registered and advertised, the refresh grant + the offline scope on registered plugins, and `/oauth/token` handling `grant_type=refresh_token`. **The refresh branch re-validates the account** — a suspended account must not renew. | `dotnet test --filter "FullyQualifiedName~PluginHandshake\|FullyQualifiedName~OAuth\|FullyQualifiedName~Plugins"` |

## Sequencing note

T5.2 removes the header fallback that `CliTests` currently drives (`X-Acme-Scope`, `-Project`,
`-Folder`, `-Export`). Those tests must move to minted claims — coordinate with T2.1, which is
already rewriting how those tests authenticate.
