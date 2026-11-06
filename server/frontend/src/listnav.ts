/**
 * Keyboard walking of a list (doc 13, Keyboard): which keys move, when a key is not ours to take,
 * and where a move lands. Pure — no DOM, no solid — so `verify/state-cases.mjs` runs it in node.
 */
import type { ProjectOverview, RunOverview } from './api.ts'
import { byActivity, matchesQuery, projectGroup, runIsOpen, type ProjectGroup } from './projects.ts'

/** The sidebar's projects: filtered by its search box, by last activity, in their groups. */
export function sideProjects(projects: readonly ProjectOverview[], asOf: string, query: string): Record<ProjectGroup, ProjectOverview[]> {
  const shown = projects.filter((p) => matchesQuery(p, query)).sort(byActivity)
  const of = (g: ProjectGroup) => shown.filter((p) => projectGroup(p, asOf) === g)
  return { active: of('active'), quiet: of('quiet'), archived: of('archived') }
}

/** The runs a sidebar project lists: its open ones, in the overview's order. */
export const openRuns = (project: ProjectOverview): RunOverview[] => project.runs.filter(runIsOpen)

/**
 * Every run the sidebar shows, top to bottom — the order Alt+ArrowUp/ArrowDown walks. Archived
 * projects count only while their group is expanded (or the search box holds text).
 */
export function sideRuns(groups: Record<ProjectGroup, ProjectOverview[]>, archivedShown: boolean): RunOverview[] {
  return [...groups.active, ...groups.quiet, ...(archivedShown ? groups.archived : [])].flatMap(openRuns)
}

/** The run a path is on: `/runs/{run}` or `/runs/{run}/{item}`. */
export function runOnPath(path: string): string | undefined {
  const [, first, run] = path.split('/')
  return first === 'runs' && run ? run : undefined
}

export type Key = { key: string; ctrlKey: boolean; metaKey: boolean; altKey: boolean; shiftKey: boolean }

export type Move = 'next' | 'prev' | 'first' | 'last'

/** Where a key would be typed rather than taken as a shortcut: a text field, a select, an editable region. */
export function typingIn(target: unknown): boolean {
  if (target === null || typeof target !== 'object') return false
  const el = target as { tagName?: unknown; isContentEditable?: unknown }
  return el.isContentEditable === true || ['INPUT', 'TEXTAREA', 'SELECT'].includes(String(el.tagName ?? '').toUpperCase())
}

const plain = (e: Key) => !e.ctrlKey && !e.metaKey && !e.altKey && !e.shiftKey

/**
 * The move a key asks of a list, or `null` when it is not ours: a modifier held, focus in a field,
 * a modal (the palette) open, or a handler before us already took it.
 */
export function listMove(e: Key & { defaultPrevented?: boolean }, typing: boolean, modal: boolean): Move | null {
  if (typing || modal || e.defaultPrevented || !plain(e)) return null
  switch (e.key) {
    case 'ArrowDown':
      return 'next'
    case 'ArrowUp':
      return 'prev'
    case 'Home':
      return 'first'
    case 'End':
      return 'last'
    default:
      return null
  }
}

/**
 * Alt+ArrowDown / Alt+ArrowUp (Option on macOS): the next or previous run. Alt alone — Alt+ArrowLeft
 * and Alt+ArrowRight stay the browser's back and forward.
 */
export function runMove(e: Key & { defaultPrevented?: boolean }, typing: boolean, modal: boolean): 'next' | 'prev' | null {
  if (typing || modal || e.defaultPrevented) return null
  if (!e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return null
  return e.key === 'ArrowDown' ? 'next' : e.key === 'ArrowUp' ? 'prev' : null
}

/**
 * Where `move` lands in `rows` from `current`: the row itself, or `undefined` for an empty list.
 * With no current row (or one not in the list) Down and Home take the first, Up and End the last.
 * The ends hold: Down on the last row stays on it, no wrap.
 */
export function step<T>(rows: readonly T[], current: T | undefined, move: Move): T | undefined {
  if (rows.length === 0) return undefined
  const at = current === undefined ? -1 : rows.indexOf(current)
  const last = rows.length - 1
  if (move === 'first') return rows[0]
  if (move === 'last') return rows[last]
  if (at < 0) return move === 'next' ? rows[0] : rows[last]
  return rows[move === 'next' ? Math.min(at + 1, last) : Math.max(at - 1, 0)]
}
