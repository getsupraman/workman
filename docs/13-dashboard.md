# 13 — The dashboard

Built from the prototype the owner approved (Workman item
`mature/ui`), on the [v1 read API](10-read-api.md) and one write: archiving a project
(`POST /api/projects/{id}/archive|unarchive`, doc 10). Everything else agents write, through the MCP
tools. Code: `server/frontend` (SolidJS, `@solidjs/router`, Vite, Tailwind 4; no other dependency).

The dashboard does not call the pre-v1 routes (`/api/projects`, `/api/watch`,
`/api/runs/{ref}/tree|brief|journal`), and they are gone from the server (unit
`mature/prev1-removal`); the tests that reached present behaviour through them now go through v1.

## Information architecture

A sidebar (the workspace switcher, for a user who may enter more than one; the search field that
opens the command palette; Overview, Needs you with its count, Agenda, Activity, Sessions, and Admin for a
user who administers a workspace; then the projects; at the foot the live state, the theme, and the
account menu — the user's name, opening *Change password* and *Sign out*) and one page beside it.
Every page is a URL.

**The workspace switcher** (`src/Shell.tsx` `TenantSwitcher`, from `GET /api/session/tenants`): a
select under the brand, its options the workspaces the user may enter, the session's selected. Shown
only when there is more than one (`showSwitcher`, `src/admin.ts`). Choosing one re-issues the cookie
(`POST /api/session/tenant`) and reloads at `/`: every read, the stream and every board belong to one
tenant, and a page of the old one would 404 in the new. A refusal is shown under it in the server's
words. The palette offers the same switch.

**The command palette** (`src/Palette.tsx`, rules in `src/palette.ts`). **Ctrl+P** (**⌘P** on
macOS) opens it from any page, taking the key from the browser's print dialog; so does **/** when
focus is not in a text field, and so does the *Search refs, items, commands* field at the top of the
sidebar, which shows the shortcut. It is a modal dialog: one input, a listbox it drives
(`aria-activedescendant`), focus held inside; ↑/↓ move, Enter opens, a click opens, Esc closes and
returns focus where it was. One input filters two groups:

- **Commands**, matched here, every word of the input against the label and a project's ref and
  title: go to Overview, Needs you, Activity, Sessions; go to Admin for a user who administers a
  workspace and to Users for a system administrator (from `GET /api/session/me`); open each project
  and its backlog (from `overview`); *Switch to workspace &lt;slug&gt;* for each other workspace the user
  may enter; go to the Agenda; toggle the theme; *Keyboard shortcuts*, which opens the **?** dialog. An empty input shows the commands alone.
- **Items**, from `GET /api/v1/search` (doc 10), 150 ms after the last keystroke; an answer for an
  input no longer typed is dropped. A row shows the item's `fullRef`, title, kind and state pill and
  its project; Enter opens it through `itemHref`. When the input is some item's exact ref, Items
  come first, so a typed ref and Enter open it. With no hits it names what was searched and that a
  ref works bare or as `run/ref` and `project/run/ref`.

**Keyboard** (the table in `src/keys.ts`, rules in `src/listnav.ts` and `src/lists.ts`, listeners in
`src/listkeys.ts`). **?** opens a dialog listing every key (`src/KeyHelp.tsx`), and so does the
palette's *Keyboard shortcuts* command; Esc or Close shuts it and returns focus. The table below is
`keysTable()` verbatim, and the dialog renders the same rows (`verify/state-cases.mjs` fails when
the two differ):

| Key | Where | What it does |
|---|---|---|
| Ctrl+P (⌘P on macOS), / | any page | opens the command palette; / only outside a text field |
| ↓ / ↑ | run page, Needs you, Agenda | selects the next / previous row as drawn and shows it in the detail panel |
| ↓ / ↑ | Backlog, Project, Activity | moves focus to the next / previous row |
| Home / End | run page, Needs you, Agenda, Backlog, Project, Activity | the first / last row |
| Enter | a focused row | opens it |
| Alt+↓ / Alt+↑ (⌥ on macOS) | any page | opens the next / previous run in the sidebar |
| Esc | palette, dialog, phone menu, detail sheet | closes it |
| Esc | run page, Needs you, Agenda, with the panel docked | clears the selection; the run page shows the run again |
| ? | any page | shows these keys |

Global keys never act while focus is in an input, textarea, select or contenteditable, or while the
palette or another modal is open, or once a handler before them took the key. They listen on the
document in the bubble phase, so the palette's and the shell's capture handlers win. Ctrl+P, **/**
and Esc keep their owners above. Alt+←/→ stay the browser's back and forward. The split handle keeps
←/→ while it has focus. No modifier may be held for the plain keys; Alt+↑/↓ is the one modifier
binding. Ctrl+↑/↓ are left alone (macOS takes them; in a field on Windows they move by paragraph).

- **Run page.** ↓/↑ walk the plan as drawn: phase heads and rows, under the current filter and
  cancelled toggle (`planOrder`). The URL keeps filter, tab and cancelled. With no selection, ↓ takes
  the first row and ↑ the last. The ends hold, with no wrap. The row scrolls into view
  (`block: nearest`).
- **Needs you.** The same, over the rows as drawn (`inboxOrder`): the person's rows, then the rows of
  each housekeeping group that is open while Housekeeping is shown. The URL is `/inbox/{item}`, and
  keeps tab and cancelled.
- **Agenda.** The same, over the rows as drawn (`agendaOrder`, `src/dates.ts`): Overdue, each day,
  then *Actionable since this morning*; an item on two rows counts once, where it first shows. The URL
  is `/agenda/{item}`, and keeps tab and cancelled.
- **Backlog, Project, Activity.** These keys move focus along the page's row links, which carry
  `data-row`, and the focus ring shows. The order is the one the page draws:
  - Backlog: the origin links and their items, with cancelled items last when shown.
  - Project: the run strips, open first, then the closed ones while expanded.
  - Activity: the lines that link an item, newest first.

  With no row focused, ↓ takes the first and ↑ the last; the ends hold. Enter is the link's own. A
  row scrolls into view only when it takes focus.
- **Alt+↓/↑** follow the sidebar's order (`sideRuns`): Active, Quiet, then Archived while it is
  expanded or searched. Each project contributes its open runs, and the search box applies. From a
  run page it starts at that run. Elsewhere, or on a run the sidebar does not list, Alt+↓ takes the
  first run and Alt+↑ the last. The ends hold. The run opens with no item selected, so ↓ then takes
  its first row.

**The project list** (`src/Shell.tsx`, rules in `src/projects.ts`):

- A search box above it filters by name, full title and ref, client-side, ignoring case. While it
  holds text, archived matches show too.
- Projects are sorted by **last activity** (the newest journal event in the project's subtree, v1
  `lastActivityAt`), newest first, and grouped: **Active** — an open run, or activity within 14 days
  of `asOf`; **Quiet** — the rest; **Archived (n)** — collapsed behind a toggle, closed by default,
  its state per viewer in `localStorage` (`workman.archived-open`, guarded for storage that
  throws).
- Each project name links to its project page (highlighted while it is open). Under it, its **open
  runs** only, each with a done/total count and a thin progress bar. A run is open unless its status
  is done, cancelled or superseded, or it has tasks and every one is finished (`runIsOpen`); an open
  run with no tasks yet stays. The rest are one muted row, *n closed*, linking to the project page.
- Hovering or focusing a run or a project shows a **tip** centred vertically on the link's right
  edge: the whole title in bold, its `fullRef` small below — the path the copy buttons copy and the
  palette finds — for scanning refs without opening anything. One tip on the page (`src/ui.tsx`
  `tipFor` / `TipLayer`), fixed to the viewport so the sidebar's overflow never clips it; a scroll
  hides it, and it never shows at phone width, where the sidebar is the menu. The links carry no
  `title`, so no native tooltip doubles it.

| URL | Page | Reads |
|---|---|---|
| `/` | **Overview** — Needs you (the top 5 as compact decision rows: kind dot, title, kind · ref with copy, age, no detail line; each opens `/inbox/{item}`; then a link to the rest), Working now, four honesty counts, then one panel per project not archived, by last activity: a serif header band (the name links to the project page; its prompt actions below it), open runs, then *Show n closed runs* (collapsed by default; closed by `runIsOpen`), each run a segmented bar with **one segment per leaf**, coloured by state; below them *n archived projects*, which shows the archived panels; the legend once below | `overview`, `inbox`, the board of every run of a project not archived |
| `/projects/{id}` | **Project** — that project's overview panel, archived or not, with its Archive / Unarchive button | `overview` |
| `/inbox`, `/inbox/{item}` | **Needs you** — the `audience: human` rows (questions, blocked work, overdue items), newest first, under a filter bar as on a run page: *All*, *Questions*, *Blocked*, *Overdue*, each with its count (`?kind=question|blocked|overdue`, All by default; the filter stays when a row is opened, and ↓/↑ walk only the rows it shows; empty under a filter: *Nothing matches this filter.*). Each is a decision row in the plan list's look (`.item`, `.on` when selected): line 1 the kind dot, the item's title (at most two lines, the whole title in a tip) and the age (the date in a tip); line 2 muted: kind · full ref with copy · actor · project · run; line 3 the server's `detail` as given (a question's intent, first line; a blocked item's *waits on: …*; an overdue item's *due 2026-11-19*, the due as written), no line when it is null; empty: *Nothing needs you.* A row opens the item beside the list (split view as on a run page, a sheet when narrow; Esc — docked or as a sheet — or Close returns to `/inbox`); ↓/↑ walk the rows. A selected row that leaves the inbox stays, struck through and marked *resolved*, until the selection moves. Then *Housekeeping for the controller* (hidden when empty, collapsed by default): one line per kind and run (*5 not verified · Run A*), each opening to one-line rows (ref · title · who · age) that open in the same panel. Unread-note rows (`unconsumed_note`) show nowhere on the page or in its counts. No row prints the MCP `call` | `inbox`; `items/{item}` when one is open |
| `/agenda`, `/agenda/{item}` | **Agenda** — the dates of the open work the user can see, in the workspace's time zone (named under the heading), from one read of `GET /api/v1/agenda?until=<today + 14 days>` (doc 10; `since` left out, so the server reads it as the start of today in the zone). One panel per group, in this order (`agendaGroups`, `src/dates.ts`): **Overdue** (soonest due first); **Today**, **Tomorrow**, then each later day up to 14 days ahead (*Fri 20 Nov*, with its `YYYY-MM-DD`), a row under the day its due falls on **in the workspace's zone** — a day due under that day, an instant under the zone's calendar day it happens on, never the UTC day — soonest due end first; then **Actionable since this morning** (the `actionable` list: ready now and became so today; latest first). Empty groups are left out; with none, *Nothing is overdue, due in the next 14 days, or newly actionable.* A row is a plan-list `.item`: the dot (stop when overdue or blocked, doing while in progress or review, the dashed wait ring with an unfinished blocker, else idle), the title (two lines at most, whole in a tip), then muted the full ref with copy, its status when not open, *on it: &lt;holder&gt;*, *waits on &lt;refs&gt;*; on the right the time (an instant's `HH:MM` in the zone, *all day* for a day due), on an overdue row *due 20 Nov* in the stop colour, on an actionable row *ready &lt;when&gt;*. A row opens the item beside the list (split view as on Needs you, a sheet when narrow; Esc docked or as a sheet, or Close, returns to `/agenda`); ↓/↑ walk the rows. An item can show twice (due and newly actionable). Live like every page | `agenda`; `items/{item}` when one is open |
| `/activity` | **Activity** — the last 300 journal events across the workspace, grouped by day | `journal?limit=300` |
| `/runs/{run}` | **Run** — plan list (filters Open / All / Needs attention / Due; phases as group headers; full titles; ref; date chips; claim holder; criteria x/y) beside the detail panel for the run | `runs/{run}/board`, `items/{run}` |
| `/runs/{run}/{item}` | the same, with `{item}` selected; a *Run* link at the right end of the filter bar, a click on the selected row (or phase head) and, with the panel docked, Esc go back to `/runs/{run}` with the filter, tab and cancelled query kept (`runPageHref`, `rowToggleHref`) | `runs/{run}/board`, `items/{item}` |
| `/items/{id}` | resolves any item: if it sits in a run it becomes `/runs/{run}/{id}`; otherwise (a project, project-level work) it is shown standalone | `items/{id}` |
| `/projects/{id}/backlog` | **Backlog** — the project's open deferrals, grouped by the run they came from | `backlog?project={id}` |
| `/admin` | **Admin** — the workspaces the user administers (every one for a system administrator), each with its member, admin and pending-invite counts, linking to its page; for a system administrator a *New workspace* form (slug, judged as the server does) and a link to Users | `/api/admin/tenants` |
| `/admin/tenants/{id}` | **Workspace** — its members, each with a role select, Remove (asks first; both locked on the last admin) and *Create reset link* (the link shown once under the row; a refusal in the server's words), and its invite links: a role and *Create invite link*, the new link shown **once** in a field with Copy, then the pending ones, each with Revoke; then **Time zone** (deadlines/8): the workspace's IANA zone in a text field offering every name the browser knows, and Save (`PATCH …/{id}/settings {timezone}`); a name the server does not know is its 400, shown under the field in its words; saving the current workspace's zone re-reads the dashboard's dates in it at once | `/api/admin/tenants`, `…/{id}/members`, `…/{id}/invites`, `…/{id}/settings` |
| `/admin/users` | **Users** — system administrator only: every user, their memberships as chips linking to each workspace's page, *Create reset link* and Disable (asks first; not oneself) / Enable | `/api/admin/users` |
| `/account` | **Account** — who is signed in; then **Connected apps** (m65): the user's own OAuth approvals in this workspace, most recently used first, each with the client's icon (or its initials), name, scopes, when it was connected and last used (relative; *last used* is when its newest token was issued, a sign-in or a refresh) and its live agent sessions; *No apps hold access to this workspace.* when there are none. *Revoke* asks first, saying the app will need to sign in again and its sessions stop, and ends the approval, every token under it, and every `wss_` session minted with one of them, releasing their claims; the list then reloads. Only the user's own apps are listed, an admin's included. Then **Calendar feed** (deadlines/8): whether the user has a feed URL in this workspace and since when (`GET /api/session/calendar`, which never returns the URL). Without one, *Create calendar URL*; with one, *Replace* and *Revoke*, each asking first (Replace: the old URL stops working at once; Revoke: the calendar app stops getting the dates). A new URL (`POST`, the only answer that carries it) is shown **once** in a field with Copy, saying that it is shown once, that the server keeps only its hash, and that it is a secret: anyone with it reads the due dates the user can see until it is replaced or revoked. Revoke is `DELETE`; both writes carry `X-Workman-Write`, and a refusal (a visiting system administrator who is not a member) shows in the server's words. Then *Change password*: the current one, the new one twice (12 characters at least). It says that the other sessions end and this one and the OAuth connections stay; a wrong current password says nothing changed | `/api/session/me`, `/api/session/apps`, `/api/session/calendar` |
| `/reset/{token}` | **Reset** — outside the dashboard, like the invite page. Names whose password it sets and until when the link works; the new password twice; once set, says every session of that user was signed out and offers the sign-in | `/api/reset/{token}` |
| `/join/{token}` | **Invite** — outside the dashboard, like the OAuth pages: no router, store, stream or sign-in gate. Names the workspace and role and until when the link works; with a session, *Join as &lt;name&gt;* and *Not you? Sign out*; without, a name and a password twice (12 characters at least) and *Create account and join*, or *Sign in first*, which shows the sign-in form and comes back. Accepting opens `/` in that workspace; a used, revoked or expired link says so and offers nothing | `/api/invite/{token}` |

`?filter=all|attention|due` (Open is the default and stays out of the URL) and `?tab=evidence|notes|activity|brief` ride on the run URL, so a
selection is deep-linkable and back/forward walk it.

**Readable links: `/r/{path}`** (server-side, `Workman.Api/App/RefLinks.cs`; not an SPA route).
`{path}` is any ref form `work_get` accepts — `project`, `project/run`, `project/run/ref`, and the
shorter unique forms — resolved by the same reader, then a 302 to the page above: a project to
`/projects/{id}`, a run to `/runs/{id}`, an item in a run to `/runs/{run}/{id}`, anything else to
`/items/{id}`. An ambiguous path answers 300 with a small page listing the unique forms as `/r`
links; an unknown one 404 with the nearby refs. The path resolves in the cookie's current tenant
(doc 08 §4). One that exists only in another tenant the user may enter is a 404 that names that
workspace with a button that switches to it (`POST /api/session/tenant`) and reloads; a tenant the user
may not enter is never looked in, so never named. It takes the dashboard cookie only (a bearer token
does not open it). Without the cookie it answers 401 with a sign-in form — the server's own page,
since the SPA's Gate would land on its not-found route — which posts to `/api/session` and reloads
the same URL, so the link survives sign-in with no credential and no target in any URL, and nothing
about the store (no id, no candidate) is said before the cookie is. `work_brief` and `work_get` print
these links as their `web` line (doc 01 §8).

The **detail panel**: crumbs, full title and qualified ref, a state pill, chips (kind, commit,
claim holder, tree, priority, severity, resolution, closed/started, then the date chips below and
*slipped n times* when the due was moved later, `slips` > 0), then tabs:

- **Overview** — what it is for, what it waits on, its done-when criteria with evidence, the verify
  command, findings against it, what is inside it.
- **Evidence** — two columns: *What the agent reported* (`work_report`, a claim) and *What was
  verified* (`work_verify`, run by someone other than the author).
- **Notes**, **Activity** — the item's own.
- **Brief** — the verbatim `work_brief`, as text. Never the main pane; its `read work://workspace/conventions` line is a link that
  opens every rule in scope below it (`GET /api/v1/items/{id}/conventions`).

### Dates

An item's `due` and `notBefore` come as written (doc 10 "Dates"): a day `YYYY-MM-DD`, which means that
whole day in the workspace's time zone, or an instant in UTC. The rules live in `src/dates.ts`, pure, and
`verify/state-cases.mjs` holds them:

- **A day is shown as a day**, never shifted by a zone: `2026-11-20` reads *20 Nov* everywhere. **An
  instant is shown in the workspace's zone**: `2026-11-20T16:00:00Z` in Europe/Rome reads *20 Nov 17:00*.
  The year is added when it is not the current one (*3 Jan 2027*). A tip gives the whole date and the zone.
- **Which zone.** The workspace's (`workspace.settings` timezone, default UTC), never the browser's: the
  server judges overdue, the agenda and the calendar in it, so the dashboard draws in the same one. Only
  an admin may read the workspace's settings, and every member may read the agenda, which names the
  zone (`zone`), so the shell reads it from there: one `GET /api/v1/agenda?until=<today>` when the store
  opens (`src/store.ts` `workspaceZone`), refreshed by every Agenda read and by a time zone saved on
  the workspace page. Until it answers, and on a server without the agenda, dates read in UTC.
- **"Now" is the server's `asOf`** of the read that drew the row (the board, the item page, the
  agenda), never this machine's clock.
- **The chips** (`dateChips`). A plan row shows them on the right before the claim holder, the detail
  panel among its chips: *not before 18 Nov* while the item is open and its not_before is still ahead
  (a day from its start in the zone); *due 20 Nov* — or, once the due has passed (a day at its end in
  the zone) while the item is open, *overdue · due 20 Nov* in the stop colour; *late* on an item closed
  as done after its due. A closed item keeps its due chip, plain. An item without dates has none.
- **The Due filter** on a run page (`?filter=due`): every open item under the run with a due, as one
  flat list without phase heads, soonest first by the instant the due ends in the zone — so a day due
  `2026-11-20` comes after an instant due `2026-11-20T16:00:00Z` that day, not before it as the text
  would sort — then in plan order. ↓/↑ walk it as drawn (`planOrder`). Empty: *No open item here has a
  due date.*
- **Needs you** has the server's `overdue` rows (`audience: human`): one per open item whose due has
  passed, kind *Overdue*, the stop dot, under its own *Overdue* filter.

The command palette's *Go to Agenda* opens the page (also found by *due*, *deadlines*, *overdue*,
*calendar*).

A claim holder is shown as the session name with the role beside it — `aw-m4-2b · controller` — when
the claim has a session (v1 `Claim.heldBySession`), else by the role alone. The same text in the
detail chip *claimed by*, the plan list's holder column, a run strip's *&lt;who&gt; is on &lt;ref&gt;* and
*Working now* (`src/format.ts` `claimHolder`).

**Copyable refs.** Wherever an item, run or project ref is shown — the detail header's ref line and
its crumbs, the plan list rows, the detail's *Waiting on* / *Findings* / *Inside* rows and a note's
*to &lt;ref&gt;*, the run page crumbs, each project panel's header and the project page heading, the run
strips, *Working now*, the inbox rows, the backlog's crumbs, group heads and rows, and the Activity
lines — a small copy icon sits beside it (`src/ui.tsx` `CopyRef`). It copies the item's
`fullRef`, the full path `project/run/ref` the server computes (doc 10 rule 2) that `work_get` /
`work_brief` resolve to exactly that item; the client never composes a ref, and copies the whole
path even where the shown ref is the bare one. A crumb line carries one copy button, for the
current page's full path; the item page shows that path as its crumb line above the title. It is a `<button>` (keyboard reachable,
`aria-label` *Copy ref &lt;ref&gt;*, a `title` tooltip); it writes with `navigator.clipboard.writeText`
and shows *Copied* in its place for a moment, or, where the clipboard is unavailable, shows the ref
selected beside it to press Ctrl+C. Inside a row link it takes its own click, so the row still
navigates and the icon only copies; clicking the ref text keeps its old behaviour.

**Archiving** (doc 02 "Archived projects"). Every project panel carries **Archive** (or, once
archived, **Unarchive** and *Archived &lt;date&gt;*). Archive first asks, inline, saying what it does: the
project leaves the active lists and the overview for *Archived*; nothing is deleted; agents still
open it by ref and repo; new work for it unarchives it; it can be unarchived here at any time.
Unarchive does not ask. The write is `POST /api/projects/{id}/archive|unarchive` with the
`X-Workman-Write` header; a failure is shown under the header band. The honesty counts and the
open-task total still include archived projects — they are the server's sums, and archiving hides a
project, it does not settle it.

**Prompt actions.** A row of buttons — on every project panel's header band (overview and project
page), under the run page's heading, and under the chips of the detail panel for any item that is not
a run or a project (those have the header rows) — each copies a ready prompt for a **fresh Claude
session**. The dashboard never acts and launches nothing. A button reads `Label` or `Label (n)`;
clicking it opens a panel under the row with the **whole** prompt text, a **Copy** button
(`navigator.clipboard.writeText`; where the clipboard is unavailable the text is selected instead and
the panel says to press Ctrl+C) and a status line that says *Copied*. Clicking the button again, or
**Close**, hides it. The buttons come from `GET /api/v1/prompts?scope={id}` (doc 10), re-read on every
live burst like the page's other reads, so a count or an action follows the data. Code:
`src/views/Prompts.tsx`. Inbox rows have none: they are the human's decisions.

### The prompt catalogue

The server builds every prompt, so the dashboard and the skills share one wording:
`server/Workman.Core/Rendering/PromptCatalogue.cs` (the entries, their predicates and templates) and
`server/Workman.Core/Board/BoardReader.Prompts.cs` (the facts they read). A prompt states the goal in
the words of [language.md](language.md) and leaves the tools to the AI: the server instructions and
the brief already teach them. *Open leaves* below are the leaves (task, follow-up, subtask, spike,
question, incident) not done, cancelled or superseded. The endpoint returns only the actions that
apply, in this order:

| scope | key · label | goal | applies when | count |
|---|---|---|---|---|
| project | `continue` · Continue | pick up where we left off and work the loop | always | — |
| project | `status` · Where are we | what landed, what is open, what needs the human; makes no writes | always | — |
| project | `plan` · Plan a feature | check the code, write a run, stop for approval (the human fills in the feature) | always | — |
| project | `cleanup` · Clean up | read every open item and loose end, check each against the code, then close, cancel, defer, merge, update and restructure into clear runs | open leaves or loose ends | the project's open leaves; the text also names the loose ends: every row `work_threads(scope:project)` lists plus the expired claims its attention block names that no stale row covers (`BoardReader.UnclosedCountAsync`) |
| project | `stepback` · Step back | question every open item against the current code and the project's goal | open leaves | — |
| project | `rules` · Review rules | read every active binding rule in scope, check each against the current code and later decisions, propose keep, reword, retire or rescope and the missing ones; never retire a red line without the human's yes for it | an active rule in scope | the active rules: workspace-wide, on the project, and on anything under it (`BoardQueries.RulesInScopeCountAsync`) |
| project | `triage` · Triage backlog | do now, later, drop or needs a decision, then a queue | n &gt; 0 | the project brief's backlog: open leaves of the backlog run, and open follow-ups anywhere under the project |
| project | `finalize` · Finalize | carry leftovers to the backlog, close the runs, archive | not archived, at least one run besides the backlog, and no such run has an unclosed leaf | — |
| run | `resume` · Resume | pick up the run where it stopped and work the loop | the run is not closed | — |
| run | `finalize-run` · Finalize run | clean up and reorder what is left, explain what the done guard still wants, offer to start the rest | not closed, and a leaf under it is done | the run's open leaves |
| run | `stepback` · Step back | question every open item against the current code and the run's goal | not closed, with open leaves | — |
| run | `rules` · Review rules | the same, for the run | not closed, with an active rule in scope | the active rules: workspace-wide, on the run and its project, and on anything under the run |
| run | `blocking` · What's blocking | every blocked item, what it waits on, who could free it; makes no writes | not closed, with a blocked leaf | the run's blocked leaves |
| run | `audit` · Did we cheat? | reported against verified, weak verify commands, forced closes, quiet deferrals; makes no writes | a leaf under the run is done | — |
| item | `work` · Work on this | do it from the worker brief and hand it back with a report | a task, follow-up or subtask that is open or ready | — |
| item | `unblock` · Investigate block | what it waits on and who could free it; makes no writes | the item is blocked | — |
| item | `stepback` · Still needed? | check it against the current code and its run's goal: keep, rewrite, merge, move, defer or cancel | a task, follow-up or subtask that is open, ready or blocked | — |

Rules every template keeps (`V1PromptTests` holds each one):

1. A prompt starts from the brief of its own scope: in its first two lines it says
   ``Start from the brief for `<ref>` `` (``the worker brief`` for `work`), and that ref is the first
   it names. The ref is the callable form: bare while unique in the workspace, else the qualified form
   that resolves to that item alone (`run-a/1.2`, never a bare `1.2` that matches two items).
2. Plain, short, and a goal rather than a recipe: it names no `work_*` tool and says what to achieve
   in the Workman words (brief, the loop, done guard, defer to the backlog, binding rule, evidence).
3. A prompt that tells the agent to close, cancel, defer, merge, move, archive or retire says, verbatim,
   *"Propose first and wait for the human's yes: list every change you intend to make, and write
   nothing until the human answers yes."* (`PromptCatalogue.ProposeAndWait`). It closes only with
   evidence it ran itself, defers as a move with a reason, cancels with a reason, and never forces
   past the done guard. A prompt that makes no writes says *"Make no writes."* and none of those verbs.
4. The noclip rule (doc 06): the text is whole — never shortened, on the server or in the panel.

A **project is named** by its title's head — the text before the first ` — `, or the whole title —
in the sidebar, the overview panel, and every crumb (run, backlog, detail). Where that name differs
from the ref (ignoring case), the ref follows in small mono type, because the ref is what agents
type. One helper holds the rule: `src/format.ts` `projectName` / `projectRefIfDifferent`, drawn by
`src/ui.tsx` `ProjectName`.

### What may be cut

Text may be shortened only in a one-line row of a list that links to a view showing it whole, and
that row carries the full text — in a tip for the sidebar's run and project links (below), in a
`title` attribute for the overview run strips and for the housekeeping rows on *Needs you*. A
decision row on *Needs you* and the overview clamps its title to two lines, with the whole title in
a `title` attribute and the item a click away. A capped list says how much it left out and links to the whole list (the overview's
*Needs you* shows five human rows, then *+N more in the inbox*). Nothing else is cut: detail views, headings,
crumbs, chips and project names wrap, and anything meant to be copied or run — a commit, a ref,
a tree — is always shown whole. A copy button copies its value whole, even
in a row whose text is cut.

## The state vocabulary

One function, `src/state.ts` `state()`, colours every dot, pill and segment. First match wins:

| state | when | drawn |
|---|---|---|
| `gone` | superseded, cancelled, or done with resolution `obsolete` (a deferral: *Moved*) | grey; left out of bars |
| `stop` | blocked; or a claim expired on an item not closed | red, ringed |
| `doing` | in progress or in review | blue, breathing (static under reduced motion) |
| `proven` | done, and either a container or a leaf whose latest verification passed and was not run by a worker | green |
| `claimed` | done leaf without that | amber — *Done, not verified* |
| `wait` | open/ready with an open `blocks` blocker | dashed ring; **not** an inbox row |
| `idle` | open | hollow ring |

Leaves are the doc 10 rollup kinds: task, followup, subtask, spike, question, incident.
`verify/state-cases.mjs` enumerates every case and fails on any change of precedence.

## Theme tokens

`data-theme` on `<html>` (`light` / `dark`), unset = follow `prefers-color-scheme`. The toggle in
the sidebar footer stores the choice in `localStorage` (`workman.theme`), guarded for storage
that throws.

| token | light | dark (VS Code Dark Modern, softened) |
|---|---|---|
| `--side` / `--side-2` | `#1d1b19` / `#2a2724` | `#181818` / `#2a2d2e` |
| `--side-ink` / `--side-mute` | `#e9e4da` / `#9c958a` | `#c8c8c8` / `#808080` |
| `--gold` (active marker) | `#d1a85a` | `#3794ff` |
| `--bg` / `--panel` | `#f6f4ef` / `#ffffff` | `#1f1f1f` / `#181818` |
| `--line` / `--line-2` | `#e8e4dc` / `#f0ede7` | `#2b2b2b` / `#262626` |
| `--ink` / `--ink-2` / `--ink-3` | `#1f1d1a` / `#5d5850` / `#948e84` | `#c8c8c8` / `#a6a6a6` / `#808080` |
| `--proj-bg` | `linear-gradient(16deg, #f3f0e9, #f8f6f1)` | `linear-gradient(16deg, #121212, #151515)` |
| `--accent` | `#4f5bd5` | `#3794ff` |
| `--proven` | `#1f8a5b` | `#4ec9b0` |
| `--claimed` | `#b7791f` | `#dcdcaa` |
| `--doing` | `#4f5bd5` | `#3794ff` |
| `--stop` | `#c2362b` | `#f14c4c` |
| `--idle` / `--gone` | `#cfc9be` / `#bdb7ad` | `#474747` / `#555555` |

Fonts: Inter (UI), JetBrains Mono (refs, commits, commands), Libre Baskerville (page titles,
project headers), from Google Fonts. Icons are inline SVG.

## Brand

The product's display name is **Workman**. Its marks come from the Supraman brand kit
(`getsupraman/brand`, outside this repo), copied byte for byte into `public/brand/` and never redrawn
or recoloured here; `src/brand.tsx` is the only place that names them.

| place | ground | files |
|---|---|---|
| sidebar brand block (`Shell.tsx`) | `--side`, dark in both themes | `workman.svg` + `workman-wordmark-on-dark.svg` |
| sign-in, consent, join, reset (`Gate.tsx`, `Connect.tsx`, `Join.tsx`, `Reset.tsx`) | `--panel`, follows the theme | `workman.svg` + `-on-light` in light, `-on-dark` in dark |
| phone topbar (`Shell.tsx`) | `--side` | `workman.svg` alone: the bar has no room for the text mark at its minimum size |
| consent page, this server's tile (`Connect.tsx` `Peers`) | `--panel` | `workman.svg` |
| favicon (`index.html`) | the browser's tab | `workman-16.svg` (16 to 24 px), `workman.svg` declared for 32 px and up |

The icon is 32 px (the kit's full icon starts there); the text mark is 17 px tall, its 16 px
minimum cap height. The brand amber lives in the mark files only: `--accent`, `--doing` and the status
tokens are unchanged, since an amber accent would read as `--claimed`. The client's tile on the
consent page is still `/connect/app-icon`, the client's own icon.

## Live updates

One `EventSource` on `/api/stream`. Frames are debounced (400 ms) into a burst; a burst re-reads
the overview, the inbox, the boards of the runs the frames name (all boards on a reconnect or a
frame with no run), and the current page's own item or journal read. A frame is never applied as
state.

## Checks

`pnpm --dir server/frontend verify` (no server) · `verify:board` and `verify:links` (a running API and
a dashboard credential) — see `server/frontend/README.md`.