# Ledger — Brief 04 "Rate limits actually reaching the free tier"

> Append-only. Written for a fresh orchestrator with zero context.

## Status
- **CODE COMPLETE, remaining work is gateway configuration.** 6 commits `c0ffee123..` (config doc
  last). Deliverable for the owner: `gateway/RATE_LIMIT_CONFIG.md`.
- Final validation, run by the orchestrator: `dotnet build` **0 errors** ·
  `RateLimitTests` **212/212** · `QuotaTests` **64/64** · `pnpm ts --force` clean ·
  all **37** gateway probe tests complete in **52 ms** without the env var (gates hold).
- Working tree holds only the **user's own** unrelated edits under
  `Acme.Web/Features/Billing/Services/Plans/**` — deliberately never staged.

## 🚨 The brief's premise was void
Brief 04 was written to stop a burst allowance double-applying on top of the free-tier quota. **There
is no burst rule anywhere in the configuration**, no applied override, and plan 2's trigger is
`manual`, so nothing fires on sign-up. Nothing to suppress; **no suppression code was
written**, deliberately. The door is marked: switching a plan to `trigger = auto` would create
the double allowance for real, and nothing guards it.

## What was actually wrong
Free-tier limits are maintained as **percentages of `Standard`**; `LimitService.cs:235`
filtered rules to `kind = "fixed"`; so the gateway applied **zero limits** for every
percentage-based plan. **All 26 free-tier accounts sit on `Unlimited` at standard limits,
campaign `organic` — no free-tier account has ever been throttled at its own rate.**
Product owner's ruling: *"Add general support for percentages — the support team loves to have the
freedom of using percentages and fixed as needed."*

## Task log (newest first)
- **3.1 config doc** — ACCEPTED. `gateway/RATE_LIMIT_CONFIG.md`, 8 sections. Blocking gaps:
  plan 2 limits nothing (its rows are `global`, invisible to a per-route query); only
  `free-eu` → plan 2 → rule set 6 exists, so rule sets 2/3/5/7 are unreachable by any
  account; rule set 3 has no burst-1 tier.
- **1.3 validity windows** (`9f8e7d6c5`) — gateway rules **cannot be archived**, so the date
  window is the only way to retire one, and the limiter read neither date. Now honoured, on base
  rule sets too. **Start inclusive, end exclusive** — matches how the gateway compares a midnight
  timestamp and errs toward ending a promotion a day early rather than a day late. Consequence to
  tell the support team: author `date_end` as the day *after* the intended last day.
- **2.1 verification probe** (`b4d5e6f70`) — read-only, against staging. **11 of 11 checkable
  real request traces agree to the request**, across four plans and six rule rows, including
  a tier expansion onto `Standard`'s burst tiers. Four traces disagree, **none refuting the code**:
  two match our figure exactly but on a different plan or window than the trace records,
  two are internally inconsistent and reproducible at no plan and no window.
  **Paid limits provably cannot move** — `Unlimited` is 100% fixed rows so `Standard` is
  never loaded on the paid path, and recomputing the whole grid both ways moves **0 of 20**
  limit points. `Standard`'s 8 percentage rows are all on legacy template 9 and all
  `base='default'`, which the code refuses outright. **Named gap: 15 of 21 free-tier rows have
  no staging trace to check against.**
- **1.2 plan code failures** (`1a2b3c4d5`) — `ResolvePlan` matched with `=` (**case-sensitive**), so a
  link written `FREE-EU` silently resolved to null and the account got standard limits. Now
  case-insensitive, per the measured gateway behaviour. A code that resolves but limits nothing now
  shows a notice and logs an **Error** naming code/plan/rule set. Deviation, accepted:
  it still falls back to standard limits rather than a blocked account — but loudly on both channels.
  Probe later confirmed the match covers all four casings and that the wildcard escaping is
  load-bearing (`%` raw → 1 hit, `\%` → 0), though no stored code contains `%`, `_` or `\`.
- **1.1 percentage limits** (`5e6f7a8b9`) — percentage rows resolve against `base_plan_id`
  for the same route/plan/tier, rounded down to a whole request **at each step** (the gateway's
  own rounding, not .NET's banker's default). Anything unresolvable — `formula`, percentage of
  `default`, missing base row, cycle, depth > 4, negative result — is **dropped with a warning**
  rather than guessed. Fixed rows pass through untouched. Deviation, good: the base's **burst
  tiers are carried across**, because rule set 6's single row is burst 0 and a tier-for-tier
  reading would limit a 10-seat team at the 1-seat rate.
- **Spike** (`c0ffee123`) — the finding above.

## Hard constraints
1. No EF migration. 2. Never `dotnet ef database update`. 3. The gateway is shared — probes read-only
and gated on `GATEWAY_LIVE_PROBE=1`. 4. `Libraries/Gateway/{Entities,Models}` are generated — never
hand-edit, never run the generator. 5. Never run `Gateway.Tests` or `Acme.Tests` unfiltered.

## Deferred follow-ups
- [ ] **The response-header shape.** We write `X-RateLimit-Remaining` = effective limit, `X-RateLimit-Bonus` = 0.
      The gateway's own manual overrides use base limit + bonus %, which makes the free-tier allowance
      **visible to the client**. A product decision, not built.
- [ ] `limit.rule` / `limit.override` / `limit.window` have **no generated models**, and
      `Plan`/`PlanRule` carry zero override fields — the spike read everything raw.
      Same for `limit.region`.
- [ ] `rule_ids` on a plan is a many-to-many **with no sequence**, so `ResolvePlan`'s
      `rule_ids[0]` is arbitrary the moment a plan carries two. Only one today.
- [ ] `plan.rule.is_override` is **not stored** on the current gateway — searching it raises; override
      rows must be found via `override_id`.
