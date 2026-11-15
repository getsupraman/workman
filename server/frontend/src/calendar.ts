/**
 * The Agenda calendar (doc 13, Agenda): its views and periods, the month grid, where each dated item
 * lands, and the URL that reproduces it. Days are the workspace zone's calendar days; the day math
 * itself is `src/dates.ts`. "Now" is the read's `asOf`. Pure — no DOM, no solid — so
 * `verify/state-cases.mjs` runs it in node.
 */
import type { CalendarRow, ItemRef } from './api.ts'
import { CLOSED } from './state.ts'
import { addDays, dateTitle, dayText, dueDay, dueEnd, isDay, monthText, weekday, zonedTime } from './dates.ts'

export type CalView = 'days' | 'week' | 'month'

/** `[view, label]`, in the switch's order; Days is the default. */
export const CAL_VIEWS: readonly (readonly [CalView, string])[] = [
  ['days', 'Days'],
  ['week', 'Week'],
  ['month', 'Month'],
]

export const DEFAULT_VIEW: CalView = 'days'

/** How many days the Days view lists from its anchor. */
export const DAYS_SPAN = 14

/** Chips a Month cell draws before it says `+N`. */
export const MONTH_CHIPS = 3

/** What the calendar shows: a view and the day it is anchored on (`null`: today). */
export type CalState = { view: CalView; date: string | null }

export const asView = (value: string | undefined | null): CalView =>
  CAL_VIEWS.some(([v]) => v === value) ? (value as CalView) : DEFAULT_VIEW

/** A real calendar day `YYYY-MM-DD`, else `null` (so `2026-02-30` is not one). */
export const asDay = (value: string | undefined | null): string | null =>
  value && isDay(value) && addDays(value, 0) === value ? value : null

/** `?view=&date=` as the page reads it: an unknown view is Days, an unreadable date is today. */
export const parseCalendar = (search: { view?: string | null; date?: string | null }): CalState => ({
  view: asView(search.view),
  date: asDay(search.date),
})

/** The day the calendar is anchored on. */
export const anchor = (cal: CalState, today: string): string => cal.date ?? today

/** The calendar's query pairs, defaults left out: Days is no `view`, today is no `date`. */
export function calendarParams(cal: CalState, today: string): [string, string][] {
  const pairs: [string, string][] = []
  if (cal.view !== DEFAULT_VIEW) pairs.push(['view', cal.view])
  if (cal.date !== null && cal.date !== today) pairs.push(['date', cal.date])
  return pairs
}

/** `query` (`''` or `?a=b`) with the calendar's pairs added after its own, so a row link or a filter keeps the view. */
export function withCalendar(query: string, cal: CalState, today: string): string {
  const q = new URLSearchParams(query)
  q.delete('view')
  q.delete('date')
  for (const [k, v] of calendarParams(cal, today)) q.set(k, v)
  return q.size > 0 ? `?${q}` : ''
}

/** The Monday on or before `day` (weeks run Monday to Sunday). */
export const monday = (day: string): string => addDays(day, -((weekday(day) + 6) % 7))

const firstOfMonth = (day: string): string => `${day.slice(0, 8)}01`

/** The 42 days of a month's grid: six weeks from the Monday on or before its 1st. */
export function monthGrid(day: string): string[][] {
  const start = monday(firstOfMonth(day))
  return Array.from({ length: 6 }, (_, w) => Array.from({ length: 7 }, (_, d) => addDays(start, w * 7 + d)))
}

/** The days a view draws, first to last; the read asks for exactly these. */
export function periodDays(cal: CalState, today: string): string[] {
  const at = anchor(cal, today)
  if (cal.view === 'month') return monthGrid(at).flat()
  const start = cal.view === 'week' ? monday(at) : at
  const n = cal.view === 'week' ? 7 : DAYS_SPAN
  return Array.from({ length: n }, (_, i) => addDays(start, i))
}

/** The read's `from` and `to`, both days, inclusive. */
export function periodRange(cal: CalState, today: string): { from: string; to: string } {
  const days = periodDays(cal, today)
  return { from: days[0]!, to: days[days.length - 1]! }
}

/** ‹ and › (and ←/→): the anchor one period earlier or later. A month moves to the 1st of the next or previous month. */
export function shiftPeriod(cal: CalState, by: -1 | 1, today: string): CalState {
  const at = anchor(cal, today)
  if (cal.view === 'days') return { ...cal, date: addDays(at, by * DAYS_SPAN) }
  if (cal.view === 'week') return { ...cal, date: addDays(monday(at), by * 7) }
  const [y, m] = at.split('-').map(Number) as [number, number]
  const next = new Date(Date.UTC(y, m - 1 + by, 1))
  return { ...cal, date: `${next.getUTCFullYear()}-${String(next.getUTCMonth() + 1).padStart(2, '0')}-01` }
}

/** The period in words: `16 Nov – 29 Nov`, `Week of 16 Nov`, `November 2026`. The bar and the header say the same. */
export function periodText(cal: CalState, today: string): string {
  const at = anchor(cal, today)
  if (cal.view === 'month') return monthText(at)
  if (cal.view === 'week') return `Week of ${dayText(monday(at), today)}`
  return `${dayText(at, today)} – ${dayText(addDays(at, DAYS_SPAN - 1), today)}`
}

/** The view switched, the anchor kept (a month shown from the 1st of the month it was on). */
export const switchView = (cal: CalState, view: CalView): CalState => ({ view, date: cal.date })

/** Month's `+N`: the Days view from that day. */
export const dayView = (day: string): CalState => ({ view: 'days', date: day })

/** ←/→ with the calendar focused: no modifier, not while typing. `null` leaves the key alone. */
export function calendarKey(e: { key: string; altKey?: boolean; ctrlKey?: boolean; metaKey?: boolean; shiftKey?: boolean }, typing: boolean): -1 | 1 | null {
  if (typing || e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return null
  if (e.key === 'ArrowLeft') return -1
  if (e.key === 'ArrowRight') return 1
  return null
}

/** One mark on the calendar: an item's due, or (`start`) the day its not_before is reached. */
export type CalEntry = {
  key: string
  item: ItemRef
  mark: 'due' | 'start'
  /** The zone's calendar day it lands on. */
  day: string
  /** `HH:MM` in the zone for an instant; `null` for a whole day. */
  time: string | null
  /** The state dot: stop when overdue or blocked, doing, the dashed wait ring, the done mark, else idle. */
  dot: 'stop' | 'doing' | 'wait' | 'claimed' | 'idle'
  /** Open and its due end before `asOf`: the stop colour. */
  overdue: boolean
  /** Done: drawn dimmed. */
  done: boolean
  /** Done after its due. */
  late: boolean
  title: string
  at: number
}

/**
 * Every mark the rows make, by the day it lands on **in the workspace's zone** (a day as itself, an
 * instant on the zone's day it happens on, never the UTC day): an item's due, and its not_before as a
 * start mark. Within a day: whole days first, then by time, then by ref.
 */
export function placeEntries(rows: readonly CalendarRow[], zone: string, asOf: string): Map<string, CalEntry[]> {
  const now = Date.parse(asOf)
  const byDay = new Map<string, CalEntry[]>()
  const put = (e: CalEntry) => byDay.set(e.day, [...(byDay.get(e.day) ?? []), e])

  for (const row of rows) {
    const done = CLOSED.has(row.status)
    const open = !done
    const end = row.due ? (row.dueEnd ? Date.parse(row.dueEnd) : dueEnd(row.due, zone)) : NaN
    const overdue = open && row.due !== null && end < now
    const wait = open && row.blockers.length > 0
    const dot: CalEntry['dot'] = done ? 'claimed' : overdue || row.status === 'blocked' ? 'stop' : row.status === 'in_progress' || row.status === 'review' ? 'doing' : wait ? 'wait' : 'idle'
    const base = { item: row.item, dot, done, late: row.late }
    const status = done ? (row.late ? ' · done late' : ' · done') : overdue ? ' · overdue' : wait ? ` · waits on ${row.blockers.map((b) => b.qualifiedRef).join(', ')}` : ''

    if (row.due) {
      put({
        ...base,
        key: `${row.item.id}:due`,
        mark: 'due',
        day: dueDay(row.due, zone),
        time: isDay(row.due) ? null : zonedTime(row.due, zone),
        overdue,
        title: `${row.item.fullRef} · ${row.item.title} — due ${dateTitle(row.due, zone)}${status}`,
        at: end,
      })
    }
    if (row.notBefore) {
      put({
        ...base,
        key: `${row.item.id}:start`,
        mark: 'start',
        day: dueDay(row.notBefore, zone),
        time: isDay(row.notBefore) ? null : zonedTime(row.notBefore, zone),
        overdue: false,
        title: `${row.item.fullRef} · ${row.item.title} — not before ${dateTitle(row.notBefore, zone)}${status}`,
        at: row.notBeforeStart ? Date.parse(row.notBeforeStart) : 0,
      })
    }
  }

  const order = (a: CalEntry, b: CalEntry) =>
    Number(a.time !== null) - Number(b.time !== null) || (a.time ?? '').localeCompare(b.time ?? '') || a.item.fullRef.localeCompare(b.item.fullRef) || a.mark.localeCompare(b.mark)
  for (const [day, list] of byDay) byDay.set(day, list.sort(order))
  return byDay
}

/** The Days view: each day of the period with something on it, in order. */
export const dayGroups = (days: readonly string[], placed: Map<string, CalEntry[]>): { day: string; entries: CalEntry[] }[] =>
  days.filter((d) => (placed.get(d)?.length ?? 0) > 0).map((day) => ({ day, entries: placed.get(day)! }))

/** A Month cell: its dues only (the start marks are Week's and Days'), the first `max`, and how many more. */
export function monthCell(entries: readonly CalEntry[] | undefined, max = MONTH_CHIPS): { shown: CalEntry[]; more: number } {
  const dues = (entries ?? []).filter((e) => e.mark === 'due')
  return dues.length <= max ? { shown: dues, more: 0 } : { shown: dues.slice(0, max - 1), more: dues.length - (max - 1) }
}

/** Month: the day's number, with the month on the 1st (`1 Dec`). */
export const cellLabel = (day: string): string => (day.endsWith('-01') ? dayText(day, day) : String(Number(day.slice(8))))

/** Week's and Month's column heads, Monday first. */
export const WEEK_HEADS = ['Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat', 'Sun'] as const
