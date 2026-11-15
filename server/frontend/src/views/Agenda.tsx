import { For, Index, Show, createEffect, on } from 'solid-js'
import { A, useNavigate, useParams, useSearchParams } from '@solidjs/router'
import * as api from '../api.ts'
import type { AgendaRow, ItemRef } from '../api.ts'
import { load, noteZone, workspaceZone } from '../store.ts'
import { agendaGroups, agendaOrder, agendaRowFacts, agendaUntil } from '../dates.ts'
import { SHOW_CANCELLED, agendaHref, itemHref } from '../nav.ts'
import { DEFAULT_FILTER, planQuery } from '../plan.ts'
import { step } from '../listnav.ts'
import { reveal, useListKeys } from '../listkeys.ts'
import { short } from '../format.ts'
import { CopyRef, Dot, Known } from '../ui.tsx'
import { SplitView } from '../split-view.tsx'
import { agendaBack } from '../back.ts'
import Detail, { asTab, type Tab } from './Detail.tsx'

/** `/agenda`, `/agenda/{item}` (doc 13): what is overdue, due today and in the next 14 days, and what became actionable this morning. */
export default function Agenda() {
  const params = useParams<{ item?: string }>()
  const [search] = useSearchParams<{ tab?: string; cancelled?: string }>()
  const tab = () => asTab(search.tab)
  const cancelled = () => search.cancelled === SHOW_CANCELLED
  const navigate = useNavigate()

  // Today is the workspace's today: once the answer names a zone other than the one asked in, the window follows it.
  const agenda = load(() => agendaUntil(Date.now(), workspaceZone()), api.agenda)
  createEffect(() => noteZone(agenda.data()?.zone))
  const page = load(() => params.item, api.item)

  const groups = () => {
    const a = agenda.data()
    return a ? agendaGroups(a) : []
  }
  const onAgenda = (id: string) => groups().some((g) => g.rows.some((r) => r.item.id === id))

  /** On this page an agenda item opens beside the list; anything else goes to its own page. */
  const href = (item: Pick<ItemRef, 'id' | 'kind'>, tabFor: Tab = tab(), cancelledFor = cancelled()) =>
    onAgenda(item.id) || item.id === params.item ? `${agendaHref(item.id)}${planQuery(DEFAULT_FILTER, tabFor, cancelledFor)}` : itemHref(item, null)

  const back = () => agendaBack()
  const close = () => navigate(back().href, { scroll: false })

  // ArrowUp/ArrowDown, Home/End walk the rows as drawn: the panel follows the selection.
  let keyed = false
  useListKeys((m) => {
    if (agenda.data() === undefined) return false
    const to = step(agendaOrder(groups()), params.item, m)
    if (to === undefined) return false
    if (to !== params.item) {
      keyed = true
      navigate(href({ id: to, kind: 'task' }), { scroll: false })
    }
    return true
  })
  createEffect(
    on(
      () => params.item,
      () => {
        if (!keyed) return
        keyed = false
        reveal('.agenda-list [aria-current="page"]')
      },
      { defer: true },
    ),
  )

  const sheetTitle = () => {
    const item = page.data()?.item
    return item ? `${item.ref} · ${short(item.title)}` : 'Item detail'
  }

  return (
    <SplitView
      title={sheetTitle()}
      open={params.item !== undefined}
      onClose={close}
      showing={params.item ?? ''}
      back={back()}
      main={
        <div class="page">
          <div class="page-h">
            <div>
              <h1>Agenda</h1>
              <div class="sub">
                What is overdue, what is due today and in the next 14 days, and what became actionable since this morning
                <Show when={agenda.data()}>{(a) => <>, in {a().zone}</>}</Show>.
              </div>
            </div>
          </div>

          <Show when={agenda.error()}>{(e) => <div class="failure">{e()}</div>}</Show>
          <Known value={agenda.data()} class="panel empty">
            {(a) => (
              <div class="stack agenda-list">
                <For each={groups()} fallback={<div class="panel empty">Nothing is overdue, due in the next 14 days, or newly actionable.</div>}>
                  {(group) => (
                    <div class="panel">
                      <div class="panel-h">
                        <h2>{group.label}</h2>
                        <Show when={group.day}>{(d) => <span class="n">{d()}</span>}</Show>
                        <span class="n">{group.rows.length}</span>
                      </div>
                      <Index each={group.rows}>
                        {(row) => (
                          <AgendaRowView
                            row={row()}
                            group={group.key}
                            zone={a().zone}
                            asOf={a().asOf}
                            href={href(row().item)}
                            selected={params.item === row().item.id}
                          />
                        )}
                      </Index>
                    </div>
                  )}
                </For>
              </div>
            )}
          </Known>
        </div>
      }
      side={
        <Show when={params.item} fallback={<div class="placeholder">Select a row to see the item here.</div>}>
          <Show when={page.missing()}>
            <div class="placeholder">There is no item with this id.</div>
          </Show>
          <Show when={page.error()}>{(e) => <div class="placeholder">{e()}</div>}</Show>
          <Show
            when={!page.missing() && page.data()}
            fallback={
              <Show when={!page.missing()}>
                <div class="placeholder">Loading…</div>
              </Show>
            }
          >
            {(p) => (
              <Detail
                page={p()}
                run={null}
                rows={new Map()}
                tab={tab()}
                cancelled={cancelled()}
                href={(item) => href(item)}
                tabHref={(t) => href(p().item, t)}
                cancelledHref={(show) => href(p().item, tab(), show)}
              />
            )}
          </Show>
        </Show>
      }
    />
  )
}

/** One agenda row, in the plan list's language: the title, then the full ref and what the row is about. */
function AgendaRowView(props: { row: AgendaRow; group: string; zone: string; asOf: string; href: string; selected: boolean }) {
  const f = () => agendaRowFacts(props.row, props.group, props.zone, props.asOf)

  return (
    <A
      class={`item inbox-row agenda-row${props.selected ? ' on' : ''}`}
      href={props.href}
      noScroll
      aria-current={props.selected ? 'page' : undefined}
    >
      <Dot k={f().dot} />
      <div class="inbox-b">
        <div class="inbox-t" title={props.row.item.title}>
          {props.row.item.title}
        </div>
        <div class="agenda-m">
          <span class="inbox-ref">{props.row.item.fullRef}</span>
          <CopyRef item={props.row.item} />
          <For each={f().facts}>{(fact) => <> · {fact}</>}</For>
        </div>
      </div>
      <div class={`item-side${f().overdue ? ' date-chip overdue' : ''}`} title={f().title}>
        {f().side}
      </div>
    </A>
  )
}
