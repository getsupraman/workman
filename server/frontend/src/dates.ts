/**
 * Dates on the dashboard (doc 13, "Dates"): an item's `due` and `notBefore` come as written — a day
 * `YYYY-MM-DD`, meaning that whole day in the workspace's time zone, or an instant in UTC (doc 10
 * "Dates"). A day is shown as a day and never shifted; an instant is shown in the workspace's zone.
 * "Now" is always the server's `asOf`, never this machine's clock. Pure — no DOM, no solid — so
 * `verify/state-cases.mjs` runs it in node.
 */
import type { AgendaRow, Agenda } from './api.ts'
import { CLOSED } from './state.ts'

/** The zone every date is read in until the server has named the workspace's. */
export const DEFAULT_ZONE = 'UTC'

const DAY = /^(\d{4})-(\d{2})-(\d{2})$/

/** A due written as a day, not an instant. */
export const isDay = (value: string): boolean => DAY.test(value)

const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec']
const WEEKDAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat']

const pad = (n: number) => String(n).padStart(2, '0')

const formatters = new Map<string, Intl.DateTimeFormat>()

/** The wall clock in `zone` at `ms`; a zone the browser does not know reads as UTC. */
function wall(ms: number, zone: string): { y: number; m: number; d: number; h: number; min: number; s: number } {
  let f = formatters.get(zone)
  if (f === undefined) {
    const options: Intl.DateTimeFormatOptions = {
      year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
    }
    try {
      f = new Intl.DateTimeFormat('en-CA', { ...options, timeZone: zone })
    } catch {
      f = new Intl.DateTimeFormat('en-CA', { ...options, timeZone: 'UTC' })
    }
    formatters.set(zone, f)
  }
  const parts = Object.fromEntries(f.formatToParts(new Date(ms)).map((p) => [p.type, p.value]))
  return { y: +parts.year!, m: +parts.month!, d: +parts.day!, h: +parts.hour! % 24, min: +parts.minute!, s: +parts.second! }
}

/** How far `zone`'s wall clock is ahead of UTC at `ms`. */
function offset(ms: number, zone: string): number {
  const w = wall(ms, zone)
  return Date.UTC(w.y, w.m - 1, w.d, w.h, w.min, w.s) - Math.floor(ms / 1000) * 1000
}

/** The calendar day an instant falls on in `zone`, as `YYYY-MM-DD`. */
export function zonedDay(instant: string | number, zone: string): string {
  const w = wall(typeof instant === 'number' ? instant : Date.parse(instant), zone)
  return `${w.y}-${pad(w.m)}-${pad(w.d)}`
}

/** `HH:MM` of an instant in `zone`. */
export function zonedTime(instant: string, zone: string): string {
  const w = wall(Date.parse(instant), zone)
  return `${pad(w.h)}:${pad(w.min)}`
}

const dayUtc = (day: string): number => {
  const [, y, m, d] = DAY.exec(day) ?? []
  return Date.UTC(Number(y), Number(m) - 1, Number(d))
}

/** `day` plus `n` calendar days. */
export function addDays(day: string, n: number): string {
  const t = new Date(dayUtc(day) + n * 86_400_000)
  return `${t.getUTCFullYear()}-${pad(t.getUTCMonth() + 1)}-${pad(t.getUTCDate())}`
}

/** The day of the week of a calendar day, 0 = Sunday … 6 = Saturday (no zone: a day is a day). */
export const weekday = (day: string): number => new Date(dayUtc(day)).getUTCDay()

/** `Mon`, `Tue`, … for 0 = Sunday … 6 = Saturday. */
export const weekdayName = (n: number): string => WEEKDAYS[n] ?? ''

/** `November 2026` for the month a day is in. */
export function monthText(day: string): string {
  const [, y, m] = DAY.exec(day) ?? []
  return `${MONTH_NAMES[Number(m) - 1]} ${y}`
}

const MONTH_NAMES = ['January', 'February', 'March', 'April', 'May', 'June', 'July', 'August', 'September', 'October', 'November', 'December']

/** The instant a day starts in `zone` (midnight there; a DST shift is settled by a second pass). */
export function dayStart(day: string, zone: string): number {
  const local = dayUtc(day)
  let at = local - offset(local, zone)
  at = local - offset(at, zone)
  return at
}

/** The instant a due is read as: a day ends at the start of the next day in the zone; an instant is itself. */
export function dueEnd(due: string, zone: string): number {
  return isDay(due) ? dayStart(addDays(due, 1), zone) : Date.parse(due)
}

/** The instant a not_before is reached: a day at its start in the zone; an instant is itself. */
export const notBeforeStart = (notBefore: string, zone: string): number =>
  isDay(notBefore) ? dayStart(notBefore, zone) : Date.parse(notBefore)

/** `20 Nov`, with the year when it is not `today`'s year. */
export function dayText(day: string, today: string): string {
  const [, y, m, d] = DAY.exec(day) ?? []
  const year = y === today.slice(0, 4) ? '' : ` ${y}`
  return `${Number(d)} ${MONTHS[Number(m) - 1]}${year}`
}

/** A due or not_before as text: a day as the day, an instant as its day and time in the zone (`20 Nov 17:00`). */
export function dateText(value: string, zone: string, asOf: string): string {
  const today = zonedDay(asOf, zone)
  if (isDay(value)) return dayText(value, today)
  return `${dayText(zonedDay(value, zone), today)} ${zonedTime(value, zone)}`
}

/** The whole date for a tip: `2026-11-20 (a whole day, Europe/Rome)` or `2026-11-20 17:00 Europe/Rome`. */
export function dateTitle(value: string, zone: string): string {
  return isDay(value) ? `${value} (the whole day, ${zone})` : `${zonedDay(value, zone)} ${zonedTime(value, zone)} ${zone}`
}

/** A day's heading on the Agenda: `Today`, `Tomorrow`, else `Fri 20 Nov`. */
export function dayHeading(day: string, today: string): string {
  if (day === today) return 'Today'
  if (day === addDays(today, 1)) return 'Tomorrow'
  return `${WEEKDAYS[new Date(dayUtc(day)).getUTCDay()]} ${dayText(day, today)}`
}

type Dated = { due?: string | null; notBefore?: string | null; status: string; late?: boolean }

/** `overdue` draws in the stop colour; `late` is a done item closed after its due. */
export type DateChip = { k: 'due' | 'overdue' | 'not_before' | 'late'; text: string; title: string }

/**
 * The chips a plan row and the item page show for an item's dates (doc 13): its due, as `overdue`
 * once it has passed while the item is still open; its not_before while it is still ahead and the item
 * open; `late` on an item closed after its due. An item with no dates has none.
 */
export function dateChips(item: Dated, zone: string, asOf: string): DateChip[] {
  const chips: DateChip[] = []
  const open = !CLOSED.has(item.status)
  const now = Date.parse(asOf)

  if (item.notBefore && open && notBeforeStart(item.notBefore, zone) > now) {
    chips.push({ k: 'not_before', text: `not before ${dateText(item.notBefore, zone, asOf)}`, title: `Not actionable before ${dateTitle(item.notBefore, zone)}` })
  }
  if (item.due) {
    const overdue = open && dueEnd(item.due, zone) <= now
    chips.push({
      k: overdue ? 'overdue' : 'due',
      text: `${overdue ? 'overdue · ' : ''}due ${dateText(item.due, zone, asOf)}`,
      title: `${overdue ? 'Overdue: due' : 'Due'} ${dateTitle(item.due, zone)}`,
    })
  }
  if (item.late) chips.push({ k: 'late', text: 'late', title: 'Closed as done after its due had passed' })
  return chips
}

/** The Agenda's window: today in the zone and the 14 days after it, as the `until` day the API takes. */
export const agendaUntil = (asOf: string | number, zone: string): string => addDays(zonedDay(asOf, zone), 14)

/** `key` is unique on the page; `day` is the zone's calendar day of a dated group. */
export type AgendaGroup = { key: string; label: string; day: string | null; rows: AgendaRow[] }

/** The calendar day a due falls on in the zone: a day is itself, an instant the day it happens on there. */
export const dueDay = (due: string, zone: string): string => (isDay(due) ? due : zonedDay(due, zone))

/**
 * The Agenda page's groups, in the order drawn (doc 13): Overdue; then Today and each later day up to
 * `until`, by the day each due falls on in the workspace's zone (`agenda.zone`), soonest first within a
 * day; then what became actionable since this morning. Empty groups are left out.
 */
export function agendaGroups(agenda: Pick<Agenda, 'asOf' | 'zone' | 'overdue' | 'due' | 'actionable'>, days = 14): AgendaGroup[] {
  const zone = agenda.zone || DEFAULT_ZONE
  const today = zonedDay(agenda.asOf, zone)
  const last = addDays(today, days)
  const byEnd = (a: AgendaRow, b: AgendaRow) => end(a, zone) - end(b, zone)

  const dated = new Map<string, AgendaRow[]>()
  for (const row of [...agenda.due].sort(byEnd)) {
    if (!row.due) continue
    // Due after asOf but still today reads Today; one the server counts as due but dated before today is overdue in all but name.
    const day = dueDay(row.due, zone) < today ? today : dueDay(row.due, zone)
    if (day > last) continue
    dated.set(day, [...(dated.get(day) ?? []), row])
  }

  const groups: AgendaGroup[] = []
  if (agenda.overdue.length > 0) groups.push({ key: 'overdue', label: 'Overdue', day: null, rows: [...agenda.overdue].sort(byEnd) })
  for (const day of [...dated.keys()].sort()) groups.push({ key: `day:${day}`, label: dayHeading(day, today), day, rows: dated.get(day)! })
  if (agenda.actionable.length > 0) {
    const since = (r: AgendaRow) => (r.readySince ? Date.parse(r.readySince) : 0)
    groups.push({ key: 'actionable', label: 'Actionable since this morning', day: null, rows: [...agenda.actionable].sort((a, b) => since(b) - since(a)) })
  }
  return groups
}

const end = (row: AgendaRow, zone: string): number =>
  row.dueEnd ? Date.parse(row.dueEnd) : row.due ? dueEnd(row.due, zone) : Number.POSITIVE_INFINITY

/** The item ids the arrow keys walk on the Agenda, top to bottom as drawn; an item on two rows counts once, where it first shows. */
export const agendaOrder = (groups: readonly AgendaGroup[]): string[] => [...new Set(groups.flatMap((g) => g.rows.map((r) => r.item.id)))]

/** What an Agenda row prints beside its title and ref (doc 13); `side` is the right-hand column. */
export type AgendaFacts = { dot: 'stop' | 'doing' | 'wait' | 'idle'; facts: string[]; side: string; title: string; overdue: boolean }

export function agendaRowFacts(row: AgendaRow, group: string, zone: string, asOf: string): AgendaFacts {
  const overdue = group === 'overdue'
  const dot = overdue || row.status === 'blocked' ? 'stop' : row.status === 'in_progress' || row.status === 'review' ? 'doing' : row.blockers.length > 0 ? 'wait' : 'idle'
  const facts: string[] = []
  if (row.status !== 'open' && row.status !== 'ready') facts.push(row.status.replace(/_/g, ' '))
  if (row.holder) facts.push(`on it: ${row.holder}`)
  if (row.blockers.length > 0) facts.push(`waits on ${row.blockers.map((b) => b.qualifiedRef).join(', ')}`)
  if (group === 'actionable' && row.due) facts.push(`due ${dateText(row.due, zone, asOf)}`)

  if (group === 'actionable') {
    const since = row.readySince ?? row.notBefore
    return { dot, facts, side: since ? `ready ${dateText(since, zone, asOf)}` : 'ready', title: since ? `Actionable since ${dateTitle(since, zone)}` : 'Actionable', overdue }
  }
  if (!row.due) return { dot, facts, side: '', title: '', overdue }
  const side = overdue ? `due ${dateText(row.due, zone, asOf)}` : isDay(row.due) ? 'all day' : zonedTime(row.due, zone)
  return { dot, facts, side, title: `${overdue ? 'Overdue: due' : 'Due'} ${dateTitle(row.due, zone)}`, overdue }
}
