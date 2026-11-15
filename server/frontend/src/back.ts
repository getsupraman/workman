/**
 * The panel's return bar: where clearing the selection goes, as the page names it. Each page's
 * close (Esc, the sheet's way back) navigates to the same `href`, so the bar and the key never
 * disagree.
 */
import { runPageHref, type Filter } from './plan.ts'
import { INBOX_FILTERS, withInboxFilter, type InboxFilter } from './inbox.ts'
import { agendaHref, inboxHref } from './nav.ts'
import { periodText, withCalendar, type CalState } from './calendar.ts'

export type Back = {
  /** The page it returns to: *Run*, *Needs you*, *Agenda*, *Calendar*. */
  label: string
  /** Which view of that page, muted after the label; empty shows none. */
  context: string
  href: string
}

/** The bar's text as read aloud and in a tip: `← Run · Title`, or `← Agenda` without context. */
export const backText = (back: Back): string => (back.context ? `← ${back.label} · ${back.context}` : `← ${back.label}`)

/** The run page with nothing selected, its filter, tab and cancelled kept. */
export const runBack = (run: string, title: string, filter: Filter, tab: string, cancelled = false): Back => ({
  label: 'Run',
  context: title,
  href: runPageHref(run, filter, tab, cancelled),
})

/** Needs you with nothing selected, its filter kept; All names no context. */
export const inboxBack = (filter: InboxFilter): Back => ({
  label: 'Needs you',
  context: filter === 'all' ? '' : (INBOX_FILTERS.find(([key]) => key === filter)?.[1] ?? ''),
  href: `${inboxHref()}${withInboxFilter('', filter)}`,
})

/** The Agenda list. While the calendar is the panel's home, Agenda passes `calendarBack` instead. */
export const agendaBack = (): Back => ({ label: 'Agenda', context: '', href: agendaHref() })

/** The Agenda with nothing selected, the calendar on the same view and period: `← Calendar · Week of 16 Nov`. */
export const calendarBack = (cal: CalState, today: string): Back => ({
  label: 'Calendar',
  context: periodText(cal, today),
  href: `${agendaHref()}${withCalendar('', cal, today)}`,
})
