import { For, Show } from 'solid-js'
import { A, useNavigate } from '@solidjs/router'
import type { Calendar } from '../api.ts'
import { dayHeading, dayText } from '../dates.ts'
import {
  CAL_VIEWS, WEEK_HEADS, calendarKey, cellLabel, dayGroups, dayView, monthCell, monthGrid, periodDays, periodText,
  placeEntries, shiftPeriod, switchView, anchor, type CalEntry, type CalState,
} from '../calendar.ts'
import { typingIn } from '../listnav.ts'
import { Dot } from '../ui.tsx'

/**
 * The Agenda calendar (doc 13): Days, Week or Month over one `GET /api/v1/calendar` read. Read-only:
 * an entry is a link that opens its item; ‹ › Today and the view switch are links, so back/forward
 * reproduce them; ←/→ move the period while focus is inside.
 */
export default function AgendaCalendar(props: {
  cal: CalState
  today: string
  data: Calendar | undefined
  error: string | undefined
  /** The page's URL for a calendar state, everything else kept. */
  calHref: (cal: CalState) => string
  /** The item opened in the panel, the calendar kept. */
  itemHref: (id: string) => string
  selected: string | undefined
}) {
  let root!: HTMLDivElement
  const navigate = useNavigate()

  const days = () => periodDays(props.cal, props.today)
  const placed = () => {
    const d = props.data
    return d ? placeEntries(d.items, d.zone, d.asOf) : new Map<string, CalEntry[]>()
  }
  const text = () => periodText(props.cal, props.today)

  const key = (e: KeyboardEvent) => {
    const by = calendarKey(e, typingIn(e.target))
    if (by === null || e.defaultPrevented) return
    e.preventDefault()
    navigate(props.calHref(shiftPeriod(props.cal, by, props.today)), { scroll: false })
    root.focus({ preventScroll: true })
  }

  const Entry = (p: { e: CalEntry }) => (
    <A
      class={`cal-e${p.e.overdue ? ' overdue' : ''}${p.e.done ? ' done' : ''}${p.e.mark === 'start' ? ' start' : ''}${props.selected === p.e.item.id ? ' on' : ''}`}
      href={props.itemHref(p.e.item.id)}
      title={p.e.title}
      noScroll
    >
      <Dot k={p.e.dot} />
      <Show when={p.e.mark === 'start'}>
        <span class="cal-start">starts</span>
      </Show>
      <Show when={p.e.time}>{(t) => <span class="cal-time">{t()}</span>}</Show>
      <span class="cal-et">{p.e.item.title}</span>
      <Show when={p.e.late}>
        <span class="cal-late">late</span>
      </Show>
    </A>
  )

  return (
    <div class="cal" ref={root} tabindex={0} onKeyDown={key} aria-label={`Calendar, ${text()}`}>
      <div class="cal-h">
        <div class="cal-nav">
          <A class="cal-btn" href={props.calHref(shiftPeriod(props.cal, -1, props.today))} aria-label="Previous period" title="Previous period (←)" noScroll>
            ‹
          </A>
          <A class="cal-btn" href={props.calHref({ ...props.cal, date: null })} noScroll>
            Today
          </A>
          <A class="cal-btn" href={props.calHref(shiftPeriod(props.cal, 1, props.today))} aria-label="Next period" title="Next period (→)" noScroll>
            ›
          </A>
        </div>
        <h2 class="cal-t">{text()}</h2>
        <div class="filters cal-views" role="group" aria-label="Calendar view">
          <For each={CAL_VIEWS}>
            {([v, label]) => (
              <A class={props.cal.view === v ? 'on' : ''} href={props.calHref(switchView(props.cal, v))} aria-current={props.cal.view === v ? 'true' : undefined} noScroll>
                {label}
              </A>
            )}
          </For>
        </div>
      </div>

      <Show when={props.error}>{(e) => <div class="failure cal-fail">{e()}</div>}</Show>
      <Show when={props.data} fallback={<Show when={!props.error}><div class="placeholder">Loading…</div></Show>}>
        <Show when={props.cal.view === 'days'}>
          <div class="cal-days">
            <For each={dayGroups(days(), placed())} fallback={<div class="empty">Nothing is due or starts in this period.</div>}>
              {(g) => (
                <div class={`cal-day${g.day === props.today ? ' today' : ''}`}>
                  <div class="cal-dh">
                    <span>{dayHeading(g.day, props.today)}</span>
                    <span class="n">{g.day}</span>
                  </div>
                  <For each={g.entries}>
                    {(e) => (
                      <A
                        class={`item cal-row${e.overdue ? ' overdue' : ''}${e.done ? ' done' : ''}${props.selected === e.item.id ? ' on' : ''}`}
                        href={props.itemHref(e.item.id)}
                        title={e.title}
                        noScroll
                      >
                        <Dot k={e.dot} />
                        <div class="inbox-b">
                          <div class="inbox-t">{e.item.title}</div>
                          <div class="agenda-m">
                            <span class="inbox-ref">{e.item.fullRef}</span>
                            <Show when={e.done}> · done</Show>
                            <Show when={e.late}>
                              {' · '}
                              <span class="cal-late">late</span>
                            </Show>
                          </div>
                        </div>
                        <div class={`item-side${e.overdue ? ' date-chip overdue' : ''}`}>
                          {e.mark === 'start' ? `starts${e.time ? ` ${e.time}` : ''}` : e.overdue ? `overdue${e.time ? ` · ${e.time}` : ''}` : (e.time ?? 'all day')}
                        </div>
                      </A>
                    )}
                  </For>
                </div>
              )}
            </For>
          </div>
        </Show>

        <Show when={props.cal.view === 'week'}>
          <div class="cal-week">
            <For each={days()}>
              {(day, i) => (
                <div class={`cal-col${day === props.today ? ' today' : ''}`}>
                  <div class="cal-ch">
                    <span>{WEEK_HEADS[i()]}</span> <b>{Number(day.slice(8))}</b>
                  </div>
                  <For each={placed().get(day) ?? []}>{(e) => <Entry e={e} />}</For>
                </div>
              )}
            </For>
          </div>
        </Show>

        <Show when={props.cal.view === 'month'}>
          <div class="cal-month">
            <For each={WEEK_HEADS}>{(h) => <div class="cal-mh">{h}</div>}</For>
            <For each={monthGrid(anchor(props.cal, props.today)).flat()}>
              {(day) => {
                const cell = () => monthCell(placed().get(day))
                const out = () => day.slice(0, 7) !== anchor(props.cal, props.today).slice(0, 7)
                return (
                  <div class={`cal-cell${out() ? ' out' : ''}${day === props.today ? ' today' : ''}`} title={dayText(day, props.today)}>
                    <div class="cal-cn">{cellLabel(day)}</div>
                    <For each={cell().shown}>{(e) => <Entry e={e} />}</For>
                    <Show when={cell().more > 0}>
                      <A class="cal-more" href={props.calHref(dayView(day))} title={`Show ${dayText(day, props.today)} in Days`} noScroll>
                        +{cell().more}
                      </A>
                    </Show>
                  </div>
                )
              }}
            </For>
          </div>
        </Show>
      </Show>
    </div>
  )
}
