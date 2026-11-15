import type { Board, BoardRow } from './api.ts'
import { CLOSED, rowState, tally, type Tally } from './state.ts'
import { SHOW_CANCELLED, runHref } from './nav.ts'
import { DEFAULT_ZONE, dueEnd } from './dates.ts'

export type Filter = 'all' | 'open' | 'attention' | 'due'

export const FILTERS: [Filter, string][] = [
  ['open', 'Open'],
  ['all', 'All'],
  ['attention', 'Needs attention'],
  ['due', 'Due'],
]

/** The filter a run page opens on, and the one its URL leaves out. */
export const DEFAULT_FILTER: Filter = 'open'

export const asFilter = (value: string | undefined): Filter =>
  value === 'all' || value === 'attention' || value === 'due' ? value : DEFAULT_FILTER

/** A run page's query: only what differs from the defaults, so an explicit `filter=all` survives a link. */
export function planQuery(filter: Filter, tab: string, cancelled = false): string {
  const q = new URLSearchParams()
  if (filter !== DEFAULT_FILTER) q.set('filter', filter)
  if (tab !== 'overview') q.set('tab', tab)
  if (cancelled) q.set('cancelled', SHOW_CANCELLED)
  return q.size > 0 ? `?${q}` : ''
}

/** The run page with nothing selected, its filter, tab and cancelled kept: the filter bar's Run link and Escape. */
export const runPageHref = (run: string, filter: Filter, tab: string, cancelled = false): string =>
  `${runHref(run)}${planQuery(filter, tab, cancelled)}`

function keep(filter: Filter, row: BoardRow, cancelled: boolean): boolean {
  if (row.item.status === 'cancelled' && !cancelled) return false
  if (filter === 'all') return true
  if (filter === 'open') return !CLOSED.has(row.item.status)
  if (filter === 'due') return !CLOSED.has(row.item.status) && !!row.due
  const k = rowState(row).k
  return k === 'stop' || k === 'claimed'
}

/** The rows strictly below `index` in a pre-order list: everything until depth climbs back. */
function descendants(rows: BoardRow[], index: number): BoardRow[] {
  const root = rows[index]
  if (root === undefined) return []

  const out: BoardRow[] = []
  for (let i = index + 1; i < rows.length; i++) {
    const row = rows[i]!
    if (row.depth <= root.depth) break
    out.push(row)
  }
  return out
}

type PlanRow = { row: BoardRow; indent: number }

/** `label` names a headless group: "Tasks" for the loose work, "Findings" for what was filed on the run itself. */
type PlanGroup = { head: BoardRow | null; label: 'Tasks' | 'Findings' | 'Due' | null; tally: Tally | null; rows: PlanRow[] }

/**
 * The plan list: loose top-level work first under "Tasks", then one group per phase with its whole
 * subtree flattened beneath it, then the findings filed directly on the run under "Findings" — a
 * task's or phase's own findings stay under their owner. Row 0 of a board is the run itself and is never listed. Cancelled
 * rows are left out unless `cancelled` is set, an emptied cancelled phase with them. A phase with no
 * children at all is judged as a row itself, so an open empty phase stays under Open.
 */
export function plan(board: Board, filter: Filter, cancelled = false, zone = DEFAULT_ZONE): PlanGroup[] {
  const rows = board.rows
  if (filter === 'due') return dueGroups(rows, zone)
  const loose: PlanRow[] = []
  const findings: PlanRow[] = []
  const phases: PlanGroup[] = []

  for (let i = 1; i < rows.length; i++) {
    const top = rows[i]!
    if (top.depth !== 1) continue

    const below = descendants(rows, i)

    if (top.item.kind === 'phase') {
      const inner = below.filter((r) => keep(filter, r, cancelled)).map((row) => ({ row, indent: row.depth - 2 }))
      const listable = filter === 'all' || below.length === 0
      if (inner.length === 0 && !(listable && keep(filter, top, cancelled))) continue
      phases.push({ head: top, label: null, tally: tally(below), rows: inner })
      continue
    }

    const into = top.item.kind === 'finding' ? findings : loose
    for (const row of [top, ...below]) {
      if (keep(filter, row, cancelled)) into.push({ row, indent: row.depth - 1 })
    }
  }

  return [
    ...(loose.length > 0 ? [{ head: null, label: 'Tasks' as const, tally: null, rows: loose }] : []),
    ...phases,
    ...(findings.length > 0 ? [{ head: null, label: 'Findings' as const, tally: null, rows: findings }] : []),
  ]
}

/**
 * The Due filter: every open item under the run with a due, flat and soonest first — by the instant
 * the due ends in the workspace's zone (a day at its end there), not by the text as written — then in
 * plan order. One headless group, so the arrow keys walk the rows as drawn.
 */
function dueGroups(rows: BoardRow[], zone: string): PlanGroup[] {
  const dated = rows
    .slice(1)
    .map((row, at) => ({ row, at, end: row.due ? dueEnd(row.due, zone) : 0 }))
    .filter(({ row }) => keep('due', row, false))
    .sort((a, b) => a.end - b.end || a.at - b.at)
  return dated.length > 0 ? [{ head: null, label: 'Due', tally: null, rows: dated.map(({ row }) => ({ row, indent: 0 })) }] : []
}

/** The plan's links in render order — each phase head, then its rows — which ArrowUp/ArrowDown walk. */
export const planOrder = (groups: PlanGroup[]): BoardRow[] =>
  groups.flatMap((g) => [...(g.head ? [g.head] : []), ...g.rows.map((r) => r.row)])

const listed = (groups: PlanGroup[]) => groups.reduce((n, g) => n + g.rows.length + (g.head ? 1 : 0), 0)

/** How many cancelled lines the plan would add under All: the number the toggle shows. */
export const cancelledLines = (board: Board): number =>
  listed(plan(board, 'all', true)) - listed(plan(board, 'all', false))

/** A child list with its cancelled entries out unless `cancelled` is set, and how many it has. */
export function withoutCancelled<T extends { item: { status: string } }>(list: T[], cancelled: boolean): { shown: T[]; count: number } {
  const open = list.filter((c) => c.item.status !== 'cancelled')
  return { shown: cancelled ? list : open, count: list.length - open.length }
}
