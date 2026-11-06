import type { Honesty, Inbox, InboxRow, ProjectOverview } from './api.ts'
import { plural } from './format.ts'

type Kind = { label: string; dot: 'stop' | 'claimed' | 'note'; many: (n: number) => string }

/** doc 10's inbox kinds, in the words a person reads them in; `many` names a housekeeping group. */
const INBOX_KINDS: Record<string, Kind> = {
  blocked: { label: 'Blocked', dot: 'stop', many: (n) => `${n} blocked` },
  blocker_finding: { label: 'Blocker finding', dot: 'stop', many: (n) => `${n} ${plural(n, 'blocker finding')} open` },
  question: { label: 'Question', dot: 'stop', many: (n) => `${n} open ${plural(n, 'question')}` },
  expired_claim: { label: 'Claim expired', dot: 'stop', many: (n) => `${n} ${plural(n, 'claim')} expired` },
  stale_in_progress: { label: 'Stalled', dot: 'stop', many: (n) => `${n} stalled` },
  assumption_due: { label: 'Assumption due', dot: 'claimed', many: (n) => `${n} with assumptions due` },
  done_unverified: { label: 'Not verified', dot: 'claimed', many: (n) => `${n} not verified` },
  unconsumed_note: { label: 'Unread note', dot: 'note', many: (n) => `${n} with unread notes` },
}

export const inboxKind = (kind: string): Kind =>
  INBOX_KINDS[kind] ?? { label: kind.replace(/_/g, ' '), dot: 'stop', many: (n) => `${n} ${kind.replace(/_/g, ' ')}` }

const KIND_ORDER = Object.keys(INBOX_KINDS)
const kindRank = (kind: string) => KIND_ORDER.indexOf(kind) + 1 || 99

/** The rows a person acts on. An unknown audience is shown to the person rather than hidden. */
export const needsYou = (rows: InboxRow[]) => rows.filter((r) => r.audience !== 'controller')

/** The count the sidebar badge and the overview print: the person's rows only, unknown until the inbox loads. */
export const needsYouCount = (box: Inbox | undefined) => (box === undefined ? undefined : needsYou(box.rows).length)

export const forController =(rows: InboxRow[]) => rows.filter((r) => r.audience === 'controller')

/**
 * The card's meta facts after the kind and ref, in reading order: who and where, then a count
 * the detail does not already say. `since` is not here; the card prints the age last.
 */
export function cardFacts(row: InboxRow): string[] {
  const facts: string[] = []
  if (row.kind === 'done_unverified' && row.actor) facts.push(`closed by ${row.actor}`)
  else if (row.actor) facts.push(`by ${row.actor}`)
  if (row.tree) facts.push(`on ${row.tree}`)
  if (row.kind === 'unconsumed_note' && row.count > 1) facts.push(`${row.count} notes`)
  return facts
}

export type HousekeepingGroup = { key: string; kind: string; run: InboxRow['run']; label: string; rows: InboxRow[] }

/** The controller's rows, one group per kind and run ("5 not verified in run-a"), kinds in `INBOX_KINDS` order. */
export function groupHousekeeping(rows: InboxRow[]): HousekeepingGroup[] {
  const groups = new Map<string, HousekeepingGroup>()
  for (const row of forController(rows)) {
    const key = `${row.kind}:${row.run?.id ?? ''}`
    const group = groups.get(key) ?? { key, kind: row.kind, run: row.run, label: '', rows: [] }
    group.rows.push(row)
    groups.set(key, group)
  }

  return [...groups.values()]
    .map((g) => ({ ...g, label: `${inboxKind(g.kind).many(g.rows.length)}${g.run ? ` in ${g.run.title}` : ''}` }))
    .sort((a, b) => kindRank(a.kind) - kindRank(b.kind) || (a.run?.title ?? '').localeCompare(b.run?.title ?? ''))
}

type InboxGroup = { kind: string; label: string; rows: InboxRow[] }

/** The summary line over the inbox: one count per kind, in the order of `INBOX_KINDS`. */
export function groupInbox(rows: InboxRow[]): InboxGroup[] {
  const groups = new Map<string, InboxRow[]>()
  for (const row of rows) groups.set(row.kind, [...(groups.get(row.kind) ?? []), row])

  return [...groups.entries()]
    .sort(([a], [b]) => kindRank(a) - kindRank(b))
    .map(([kind, list]) => ({ kind, label: inboxKind(kind).label, rows: list }))
}

type HonestyCell = { n: number; label: string; bad: boolean }

/** The four counts beside "Working now". A zero needs no decision, so it gets no hue. */
export function honestyCells(honesty: Honesty, projects: ProjectOverview[]): HonestyCell[] {
  const open = openTasks(projects)
  return [
    { n: honesty.doneUnverified, label: 'closed without independent proof', bad: honesty.doneUnverified > 0 },
    { n: honesty.forced, label: 'forced closures', bad: honesty.forced > 0 },
    { n: honesty.followupsOpen, label: 'follow-ups open', bad: false },
    { n: open, label: 'tasks open', bad: false },
  ]
}

export const openTasks = (projects: ProjectOverview[]) =>
  projects.reduce((n, p) => n + p.rollup.open + p.rollup.inProgress + p.rollup.review + p.rollup.blocked, 0)
