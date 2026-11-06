/**
 * The row order of the list pages that open an item (doc 13, Keyboard): each page draws its rows from
 * here and tags each row link with `data-row` = its key, so ArrowUp/ArrowDown walk exactly what is
 * drawn. Pure, so `verify/state-cases.mjs` runs it in node.
 */
import type { Backlog, ItemRef, JournalEvent, ProjectOverview, RunOverview } from './api.ts'
import { day } from './format.ts'
import { runIsOpen } from './projects.ts'

export type Day = { day: string; events: JournalEvent[] }

/** Newest first, one group per calendar day in the reader's zone. */
export function activityDays(events: readonly JournalEvent[]): Day[] {
  const days: Day[] = []
  for (const event of [...events].reverse()) {
    const d = day(event.at)
    const last = days[days.length - 1]
    if (last?.day === d) last.events.push(event)
    else days.push({ day: d, events: [event] })
  }
  return days
}

export const eventRow = (event: Pick<JournalEvent, 'seq'>): string => `ev:${event.seq}`

/** Activity: the events that link to an item, as drawn — newest day first. */
export const activityOrder = (days: readonly Day[]): string[] =>
  days.flatMap((d) => d.events.filter((e) => e.item !== null).map(eventRow))

export const originRow = (origin: Pick<ItemRef, 'id'>): string => `from:${origin.id}`
export const itemRow = (item: Pick<ItemRef, 'id'>): string => `item:${item.id}`

/** Backlog: each group's origin link, then its items; the cancelled ones last, when shown. */
export const backlogOrder = (backlog: Pick<Backlog, 'groups'>, cancelled: readonly Pick<ItemRef, 'id'>[]): string[] => [
  ...backlog.groups.flatMap((g) => [...(g.origin ? [originRow(g.origin)] : []), ...g.items.map(itemRow)]),
  ...cancelled.map(itemRow),
]

/** A project's runs as its panel draws them: the open ones, then (behind a toggle) the closed. */
export function projectRuns(project: Pick<ProjectOverview, 'runs'>): { open: RunOverview[]; closed: RunOverview[] } {
  return { open: project.runs.filter(runIsOpen), closed: project.runs.filter((r) => !runIsOpen(r)) }
}

export const runRow = (run: Pick<RunOverview, 'item'>): string => `run:${run.item.id}`

/** Project: its run strips, open then closed; a collapsed closed list is not drawn, so not walked. */
export function projectOrder(project: Pick<ProjectOverview, 'runs'>): string[] {
  const { open, closed } = projectRuns(project)
  return [...open, ...closed].map(runRow)
}
