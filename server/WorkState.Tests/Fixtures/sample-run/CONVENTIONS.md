# Conventions — binding for every task in this run

Read this before writing code. It is referenced by path from every brief; nothing here is repeated in a brief.

## Where you are

- Worktree: `C:\work\acme\api\.claude\worktrees\token-hardening-2`, branch `feat/token-hardening-3`.
  Work here. Do **not** `cd` to the main checkout.
- Repo conventions are authoritative and already written: `CLAUDE.md`, `AGENTS.md`,
  `.github/instructions/backend.instructions.md`. Read what your task touches; don't paraphrase them back.

## Hard safety rules

1. **Never apply a migration.** `acme_dev` is a shared Postgres. `dotnet ef database update` is
   forbidden. Adding a migration file is fine; applying it is the owner's deploy step. Schema changes are
   expand/contract: add nullable, never rename or drop ahead of a deploy.
2. **Never commit.** Leave changes in the working tree and report. The orchestrator commits.
3. **Never `git stash`.** The stash stack is shared with other worktrees and other sessions.
4. **No secrets in tracked source.** If a task hands you a credential, it goes to configuration, never
   into a `.cs` file. Grep your own diff before reporting.
5. **Never widen an account boundary.** `AccountScopeInterceptor` and the `WhereAccount*` predicates
   are the whole boundary — there are no EF global query filters, deliberately. Only
   `IAccountContext.IsOperator` crosses accounts.
6. **Do not restructure the list endpoints or their cache keys.** The edge cache serves 71% of list
   reads; a careless change there is a large, silent bill. Out of scope for every task in
   this run unless the task says otherwise in so many words.

## Comments — this repo is strict about them

Default verdict on any comment is **delete**. A comment survives only if it states, in the **present
tense**, something the code cannot state itself — typically an invariant owned by another file, which it
**points at** rather than paraphrases (`// AccountId is interceptor-owned; see AccountScopeInterceptor.`).

- No history: no "used to", "previously", no naming a retired construct, no narrating what your change did.
- No teaching: the reader knows C#, EF Core, ASP.NET. Do not explain framework features.
- Survivors get **one line**. Never add `/// <summary>` during a cleanup. Never put `///` on a `record`'s
  positional parameters — that is CS1587.
- User-facing text stays as written: UI copy, validation messages and API error messages are
  product text, not comments. Leave them alone.

## Tests

- New tests that read or write the database must carry `[RequiresDatabaseFact]` / `[RequiresDatabaseTheory]`
  (`Acme.Tests/RequiresDatabase.cs`), not `[Fact]` / `[Theory]`. Unset, they report **Skipped**, never passed.
- **Red-check every test that defends a fix**: confirm it fails against the un-fixed code and report the
  failure count. A test written against the fixed code, that has never been seen red, is not evidence.
  Then name the *wrong* implementations your fixture still passes, and defeat those too.
- A test that passes against the broken code is deleted, not kept.

## Verification

- Run the narrowest command that can fail for your task; the orchestrator runs the full suite at gates.
- Read the real result line (`Passed! - Failed: 0, Passed: N`), not the exit code of a pipe.
- `dotnet build Acme.slnx -v q --nologo` must be clean before you report.

## Report format — return exactly this, no code, no diffs

```
STATUS:     done | blocked | partial
FILES:      every path you changed, one per line
VERIFY:     the command you ran, and its real result line
RED-CHECK:  the test(s), and the failure count against un-fixed code — or "n/a, no fix defended"
DEVIATIONS: where the brief was wrong, or reality differed. Say so plainly; this is the most valuable line.
ASSUMPTIONS:what you decided that the brief did not settle
RISKS:      what could bite later
NOT DONE:   anything in the brief you did not deliver, and why
```
