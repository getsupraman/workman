import type { Honesty, Inbox, InboxRow, ProjectOverview } from './api.ts'
import { plural, projectName, short } from './format.ts'

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
export const needsYou = (rows: InboxRow[]) => shown(rows).filter((r) => r.audience !== 'controller')

/** Unread notes reach their reader in its brief; the page shows none of them (doc 13). */
const HIDDEN_KINDS = new Set(['unconsumed_note'])
const shown = (rows: InboxRow[]) => rows.filter((r) => !HIDDEN_KINDS.has(r.kind))

/** The count the sidebar badge and the overview print: the person's rows only, unknown until the inbox loads. */
export const needsYouCount = (box: Inbox | undefined) => (box === undefined ? undefined : needsYou(box.rows).length)

export const forController = (rows: InboxRow[]) => shown(rows).filter((r) => r.audience === 'controller')

/** Needs you's filter bar, as the run page's: `?kind=`, All by default. */
export const INBOX_FILTERS = [
  ['all', 'All'],
  ['question', 'Questions'],
  ['blocked', 'Blocked'],
] as const

export type InboxFilter = (typeof INBOX_FILTERS)[number][0]

export const asInboxFilter = (value: string | undefined): InboxFilter =>
  INBOX_FILTERS.some(([key]) => key === value) ? (value as InboxFilter) : 'all'

/** The person's rows under a filter; All keeps a kind the bar does not name. */
export const filterInbox = (rows: InboxRow[], filter: InboxFilter): InboxRow[] =>
  filter === 'all' ? rows : rows.filter((r) => r.kind === filter)

/** `query` (a `?…` string or empty) with the filter added; All adds nothing. */
export function withInboxFilter(query: string, filter: InboxFilter): string {
  if (filter === 'all') return query
  const q = new URLSearchParams(query)
  q.set('kind', filter)
  return `?${q}`
}

/**
 * A row's who and where after the kind and ref, in reading order, then a count the detail does
 * not already say. `since` is not here; the row prints the age on its first line.
 */
export function cardFacts(row: InboxRow): string[] {
  const facts: string[] = []
  if (row.kind === 'done_unverified' && row.actor) facts.push(`closed by ${row.actor}`)
  else if (row.actor) facts.push(`by ${row.actor}`)
  if (row.tree) facts.push(`on ${row.tree}`)
  if (row.kind === 'unconsumed_note' && row.count > 1) facts.push(`${row.count} notes`)
  return facts
}

type ProjectLike = { item: { ref: string; title: string } }

/** `item.fullRef` starts with the project's ref; its name when the overview knows it, else that segment. */
export function rowProject(row: InboxRow, projects: readonly ProjectLike[] | undefined): string {
  const segment = row.item.fullRef?.split('/')[0] ?? ''
  const project = projects?.find((p) => p.item.ref === segment)
  return project ? projectName(project.item) : segment
}

/**
 * What a decision row prints, in reading order (doc 13): line 1 the dot, the title and the age; line 2
 * the muted kind, the full ref, then who and where; line 3 the server's detail, or no line at all. The
 * row's `call` is not here, so no view can paint it.
 */
export type DecisionRow = {
  dot: Kind['dot']
  kind: string
  title: string
  fullRef: string
  facts: string[]
  detail: string | null
  since: string
}

export function decisionRow(row: InboxRow, projects?: readonly ProjectLike[]): DecisionRow {
  const facts = cardFacts(row)
  const project = rowProject(row, projects)
  if (project) facts.push(project)
  if (row.run) facts.push(short(row.run.title))
  const kind = inboxKind(row.kind)
  return {
    dot: kind.dot,
    kind: kind.label,
    title: row.item.title,
    fullRef: row.item.fullRef,
    facts,
    detail: row.detail?.trim() ? row.detail : null,
    since: row.since,
  }
}

/** A housekeeping row on one line: ref · title · who. The view adds the age. */
export const choreRow = (row: InboxRow) => ({
  ref: row.item.qualifiedRef,
  title: row.item.title,
  actor: row.actor,
  since: row.since,
})

/** `line` is what the page prints for the group: "5 not verified · Run A". */
export type HousekeepingGroup = { key: string; kind: string; run: InboxRow['run']; label: string; line: string; rows: InboxRow[] }

/** The controller's rows, one group per kind and run ("5 not verified in run-a"), kinds in `INBOX_KINDS` order. */
export function groupHousekeeping(rows: InboxRow[]): HousekeepingGroup[] {
  const groups = new Map<string, HousekeepingGroup>()
  for (const row of forController(rows)) {
    const key = `${row.kind}:${row.run?.id ?? ''}`
    const group = groups.get(key) ?? { key, kind: row.kind, run: row.run, label: '', line: '', rows: [] }
    group.rows.push(row)
    groups.set(key, group)
  }

  return [...groups.values()]
    .map((g) => {
      const many = inboxKind(g.kind).many(g.rows.length)
      return { ...g, label: `${many}${g.run ? ` in ${g.run.title}` : ''}`, line: `${many}${g.run ? ` · ${short(g.run.title)}` : ''}` }
    })
    .sort((a, b) => kindRank(a.kind) - kindRank(b.kind) || (a.run?.title ?? '').localeCompare(b.run?.title ?? ''))
}

/**
 * The item ids ArrowUp/ArrowDown walk on Needs you, top to bottom as drawn: the person's rows, then
 * the rows of each housekeeping group that is open while Housekeeping itself is shown. An item on
 * two rows counts once, where it first appears.
 */
export function inboxOrder(
  human: readonly InboxRow[],
  groups: readonly HousekeepingGroup[],
  housekeepingShown: boolean,
  open: ReadonlySet<string>,
): string[] {
  const rows = [...human, ...(housekeepingShown ? groups.filter((g) => open.has(g.key)).flatMap((g) => g.rows) : [])]
  return [...new Set(rows.map((r) => r.item.id))]
}

/**
 * The person's rows after a read, keeping the selected one: an item that left the inbox while it is
 * open beside the list (answered, unblocked) stays where it was, marked `resolved`, until the
 * selection moves. `before` is what was drawn last time.
 */
export function keepSelected(
  before: readonly InboxRow[],
  now: readonly InboxRow[],
  selected: string | undefined,
): { rows: InboxRow[]; resolved: string | null } {
  if (selected === undefined || now.some((r) => r.item.id === selected)) return { rows: [...now], resolved: null }
  const at = before.findIndex((r) => r.item.id === selected)
  if (at < 0) return { rows: [...now], resolved: null }
  const rows = [...now]
  rows.splice(Math.min(at, rows.length), 0, before[at]!)
  return { rows, resolved: selected }
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
