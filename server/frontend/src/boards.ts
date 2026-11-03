import type { Overview } from './api.ts'

/** `/api/v1/boards` refuses more ids than this; past it, one read of every run is the same single call. */
export const BOARDS_MAX = 200

/**
 * The one boards read a refresh makes: `'all'` for every run, those ids, or `null` for none. Named runs are re-read,
 * and so is any run the overview lists whose board is not held yet. An archived project's bars are not drawn until it
 * is shown, which falls back to the rollup.
 */
export function boardsRead(next: Overview, runs: 'all' | string[], held: Record<string, unknown>): 'all' | string[] | null {
  if (runs === 'all') return 'all'

  const ids = next.projects.filter((p) => !p.archivedAt).flatMap((p) => p.runs.map((r) => r.item.id))
  const wanted = ids.filter((id) => runs.includes(id) || held[id] === undefined)
  if (wanted.length === 0) return null
  return wanted.length > BOARDS_MAX ? 'all' : wanted
}
