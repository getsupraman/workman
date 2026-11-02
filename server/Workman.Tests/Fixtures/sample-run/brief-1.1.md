# Task 1.1 — `B2`: exported files are served by id with an account filter only

**Read `.claude/orchestration/api-token-hardening/CONVENTIONS.md` in full first. It is binding —
especially the red lines and the comment doctrine.** Branch is already checked out; do not create one.

## The defect

`ExportStore.Find` (`Acme.Api/src/Exports/ExportStore.cs:47-52`) is
`_.Id == exportId && _.AccountId == accountContext.AccountId` and nothing else. Any member of an account
can iterate sequential integer export ids and download every colleague's project report, every shared-folder
archive and every other user's private export in the account.

`ExportOwnerResolver.ResolveAsync(exportId)` (`Features/Exports/Services/ExportOwnerResolver.cs:41`) **already
computes which owner an export belongs to** — Project, Folder or User, via `GetOwners` at `:174-179`. The
download path simply never asks it.

## The fix

**Put the read check on `IExportOwnerHandler` and make the two resolver methods that reach `Find` ask it.**

### Where exactly — this is the part the spec originally got wrong, so read it carefully

Gate **`ExportOwnerResolver.Download` (`:101`) and `ExportOwnerResolver.Convert` (`:109`)**.

Do **not** gate `ServeExportAsync` in `src/Exports/Download/ExportResponseExtensions.cs`. That extension method is
a thin wrapper over those two, and gating it would cover only the three controllers that happen to use the
wrapper while leaving every other caller of `Download`/`Convert` open — including
`PartnerExportHandler.Download`, which reaches `Download` directly and bypasses `ServeExportAsync` entirely.
Gating the resolver methods closes the three web routes **and** the partner route **and** every future caller,
which is the entire point of doing this here rather than at three call sites.

### Steps

1. **`Acme.Api/src/Exports/Abstractions/IExportOwnerHandler.cs`** (interface at `:20`) — add one member that
   answers "may the current caller read this export". A `Task<bool>` probe is preferable to a throwing member so
   the resolver decides the response shape in one place; name it for what it asks.

2. **`Features/Exports/Services/Owners/ProjectExportOwnerHandler.cs`** — implement it with `ProjectAccessService`.
   **`projectAccess` is already injected at `:29`** (used at `:425` for `RequireDelete`), so no ctor change.
   Use **`GetCurrentUserPermissions(projectId).Read`** — the non-throwing probe at `ProjectAccessService.cs:13`.
   It returns `ProjectPermissions(false, false, false, false)` when there is no `ProjectMembers` row, so absent
   membership is already fail-closed. **Do not add a new member to `ProjectAccessService`; it has what you need.**

3. **`Features/Exports/Services/Owners/FolderExportOwnerHandler.cs`** — implement it as **account-scoped only:
   return true.** This is deliberate and is NOT an oversight. `FolderMember` is a "my folders" bookmark, not
   an ACL — membership enforcement was built once and withdrawn because users rejected it. The constraint is
   recorded on the `FolderMember` entity; read that remark before you touch this. **Adding a membership check
   here is an explicit task failure.**

4. **`Features/Exports/Services/Owners/UserExportOwnerHandler.cs`** — implement it as
   `userId == accountContext.UserId`. A user's private exports are theirs.

5. **`Features/Exports/Services/ExportOwnerResolver.cs`** — in `Download` and `Convert`, resolve the owner
   and consult the new member before returning bytes. A denial must be indistinguishable from "not found" to
   the caller — do not leak the export's existence.

## Constraints specific to this task

- **The partner path reaches `Download`.** `GET /api/partner/exports/{id}/download` →
  `PartnerExportHandler.Download` → `IExportOwnerResolver.Download`. It will now be checked. That is
  intended. It is expected to pass in practice because `PartnerSyncService` grants every active account user
  `ProjectPermission.Owner` on a synced project — but **do not rely on that, and do not special-case partners.**
  Write a test that a partner-shaped caller (a member with an Owner `ProjectMembers` row) still downloads.
- **`ResolveAsync` throws `InvalidOperationException` when an export has no owner** (`:174`). Decide what an
  export with no owner row should do on a read path and say which you chose in `ASSUMPTIONS`. Fail closed.
- Do not change `ProjectAccessService`, `ServeExportAsync`, `ExportStore`, or any partner file.
- Do not change `ExportOwnerResolver.Delete`, `Retry`, `Save` or the upload paths. Read only.

## Done criteria

- A member with no `ProjectMembers` row for project P cannot download, convert or otherwise obtain the bytes of
  an export attached to P, through any of: `GET /api/exports/{id}`, `GET /api/projects/exports/{id}` file route,
  `GET /api/folders/exports/{id}` file route, or `ExportOwnerResolver.Download` called directly.
- A member **with** a `ProjectMembers` row carrying Read still gets all of the above.
- A user export is readable by its owning user and by nobody else in the account.
- A folder export remains readable by any member of the account.
- `dotnet build Acme.slnx` clean.

## Verify

```powershell
dotnet build Acme.slnx
dotnet test Acme.Tests/Acme.Tests.csproj --filter "FullyQualifiedName~Export"
```

Then the full suite once, to prove nothing else regressed:
```powershell
dotnet test Acme.Tests/Acme.Tests.csproj
```

## Red-check — required

Write the negative tests first, confirm them **FAILING** against the current un-fixed code, record the failure
count and the assertion message, then implement and confirm green. Report both numbers.

Then name the wrong implementations your fixture would still pass — specifically, would it pass an
implementation that checks the account only? that checks the project but not the user export? that returns true
for every handler? Add the case that defeats each, or say why it cannot be written.

A DB-touching test carries `[RequiresDatabaseFact]`, never `[Fact]`.

## Report

Use the report format at the bottom of `CONVENTIONS.md`. No code, no diffs. `DEVIATIONS` is the line I care
most about — tell me anything in this brief that turned out factually wrong about the codebase.
