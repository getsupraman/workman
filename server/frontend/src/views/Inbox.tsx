import { For, Index, Show, createEffect, createMemo, createSignal, on } from 'solid-js'
import { A, useNavigate, useParams, useSearchParams } from '@solidjs/router'
import * as api from '../api.ts'
import type { InboxRow, ItemRef } from '../api.ts'
import { inbox, load, overview } from '../store.ts'
import {
  INBOX_FILTERS,
  asInboxFilter,
  choreRow,
  decisionRow,
  filterInbox,
  groupHousekeeping,
  inboxKind,
  inboxOrder,
  keepSelected,
  needsYou,
  withInboxFilter,
  type HousekeepingGroup,
  type InboxFilter,
} from '../inbox.ts'
import { SHOW_CANCELLED, inboxHref, itemHref } from '../nav.ts'
import { DEFAULT_FILTER, planQuery } from '../plan.ts'
import { step } from '../listnav.ts'
import { reveal, useListKeys } from '../listkeys.ts'
import { ago, plural, short } from '../format.ts'
import { CopyRef, Dot, Known } from '../ui.tsx'
import { SplitView } from '../split-view.tsx'
import Detail, { asTab, type Tab } from './Detail.tsx'

/**
 * One decision row, in the run plan list's language (`.item`, `.on`): the title first, the kind and
 * full ref muted under it, then the server's detail line. `compact` (the overview) keeps the title, the kind,
 * the ref and the age.
 */
export function InboxRowView(props: {
  row: InboxRow
  asOf: string
  href?: string
  selected?: boolean
  /** It left the inbox while open beside the list: kept, struck through, until the selection moves. */
  resolved?: boolean
  compact?: boolean
}) {
  const m = () => decisionRow(props.row, overview()?.projects)

  return (
    <A
      class={`item inbox-row${props.selected ? ' on' : ''}${props.resolved ? ' resolved' : ''}`}
      href={props.href ?? itemHref(props.row.item, props.row.run)}
      noScroll
      aria-current={props.selected ? 'page' : undefined}
    >
      <Dot k={m().dot} />
      <div class="inbox-b">
        <div class="inbox-t" title={m().title}>
          {m().title}
        </div>
        <div class="inbox-m">
          {m().kind} · <span class="inbox-ref">{m().fullRef}</span>
          <CopyRef item={props.row.item} />
          <Show when={!props.compact}>
            <For each={m().facts}>{(fact) => <>· {fact} </>}</For>
          </Show>
        </div>
        <Show when={!props.compact && m().detail}>{(detail) => <div class="inbox-d">{detail()}</div>}</Show>
      </div>
      <div class="item-side" title={new Date(m().since).toLocaleString()}>
        {props.resolved ? 'resolved' : ago(m().since, props.asOf)}
      </div>
    </A>
  )
}

/** A housekeeping row on one line: ref · title · who, and the age. */
function ChoreRowView(props: { row: InboxRow; asOf: string; href?: string; selected?: boolean }) {
  const c = () => choreRow(props.row)

  return (
    <A
      class={`item chore-row${props.selected ? ' on' : ''}`}
      href={props.href ?? itemHref(props.row.item, props.row.run)}
      noScroll
      aria-current={props.selected ? 'page' : undefined}
    >
      <span />
      <div class="chore-t" title={c().title}>
        <span class="item-ref">{c().ref}</span>
        {c().title}
        <Show when={c().actor}>{(actor) => <span class="chore-who"> · {actor()}</span>}</Show>
      </div>
      <div class="item-side" title={new Date(c().since).toLocaleString()}>
        {ago(c().since, props.asOf)}
      </div>
    </A>
  )
}

/** One housekeeping group: one line per kind and run. Open state lives with the page, keyed by group, so a refresh keeps it. */
function Group(props: {
  group: HousekeepingGroup
  asOf: string
  open: boolean
  toggle: () => void
  selected: string | undefined
  href: (row: InboxRow) => string
}) {
  return (
    <>
      <button type="button" class="item chore-group" aria-expanded={props.open} onClick={() => props.toggle()}>
        <Dot k={inboxKind(props.group.kind).dot} />
        <span class="item-t">{props.group.line}</span>
        <span class="item-side">{props.open ? '▾' : '▸'}</span>
      </button>
      <Show when={props.open}>
        <Index each={props.group.rows}>
          {(row) => <ChoreRowView row={row()} asOf={props.asOf} href={props.href(row())} selected={props.selected === row().item.id} />}
        </Index>
      </Show>
    </>
  )
}

export default function Inbox() {
  const params = useParams<{ item?: string }>()
  const [search] = useSearchParams<{ tab?: string; cancelled?: string; kind?: string }>()
  const tab = () => asTab(search.tab)
  const filter = () => asInboxFilter(search.kind)
  const cancelled = () => search.cancelled === SHOW_CANCELLED
  const navigate = useNavigate()
  const page = load(() => params.item, api.item)

  const [showHousekeeping, setShowHousekeeping] = createSignal(false)
  const [openGroups, setOpenGroups] = createSignal<ReadonlySet<string>>(new Set())
  const toggle = (key: string) => {
    const next = new Set(openGroups())
    if (!next.delete(key)) next.add(key)
    setOpenGroups(next)
  }

  const rows = () => inbox()?.rows ?? []
  const groups = () => groupHousekeeping(rows())
  const human = createMemo<{ rows: InboxRow[]; resolved: string | null }>(
    (before) => {
      const all = needsYou(rows())
      const now = filterInbox(all, filter())
      // Filtered out is not resolved: only an item that left the inbox is kept in place.
      if (params.item !== undefined && all.some((r) => r.item.id === params.item)) return { rows: now, resolved: null }
      return keepSelected(before.rows, now, params.item)
    },
    { rows: [], resolved: null },
  )
  const inInbox = (id: string) => rows().some((r) => r.item.id === id)

  /** On this page an inbox item opens beside the list; anything else goes to its own page. */
  const href = (item: Pick<ItemRef, 'id' | 'kind'>, tabFor: Tab = tab(), cancelledFor = cancelled()) =>
    inInbox(item.id) || item.id === params.item
      ? `${inboxHref(item.id)}${withInboxFilter(planQuery(DEFAULT_FILTER, tabFor, cancelledFor), filter())}`
      : itemHref(item, null)
  const rowHref = (row: InboxRow) => href(row.item)

  /** The filter bar's link: the selection and its tab stay, only the filter changes. */
  const filterHref = (f: InboxFilter) =>
    `${params.item ? inboxHref(params.item) : inboxHref()}${withInboxFilter(planQuery(DEFAULT_FILTER, tab(), cancelled()), f)}`

  const close = () => navigate(`${inboxHref()}${withInboxFilter('', filter())}`, { scroll: false })

  // ArrowUp/ArrowDown, Home/End walk the rows as drawn: the panel follows the selection.
  let keyed = false
  useListKeys((m) => {
    if (inbox() === undefined) return false
    const order = inboxOrder(human().rows, groups(), showHousekeeping(), openGroups())
    const to = step(order, params.item, m)
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
        reveal('.inbox-list [aria-current="page"]')
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
      main={
        <div class="page">
          <div class="page-h">
            <div>
              <h1>Needs you</h1>
              <div class="sub">The questions and blocked work that wait on a decision from you, newest first.</div>
            </div>
          </div>

          <Known value={inbox()} class="panel empty">
            {(box) => {
              const chores = () => groups().reduce((n, g) => n + g.rows.length, 0)
              const waiting = () => needsYou(box().rows)

              return (
                <div class="stack inbox-list">
                  <div>
                    <div class="panel">
                      <Show when={waiting().length > 0}>
                        <nav class="filters" aria-label="Filter Needs you">
                          <For each={INBOX_FILTERS}>
                            {([key, label]) => (
                              <A href={filterHref(key)} class={filter() === key ? 'on' : ''} noScroll>
                                {label} <b class="filter-n">{filterInbox(waiting(), key).length}</b>
                              </A>
                            )}
                          </For>
                        </nav>
                      </Show>
                      <Index
                        each={human().rows}
                        fallback={<div class="empty">{waiting().length > 0 ? 'Nothing matches this filter.' : 'Nothing needs you.'}</div>}
                      >
                        {(row) => (
                          <InboxRowView
                            row={row()}
                            asOf={box().asOf}
                            href={rowHref(row())}
                            selected={params.item === row().item.id}
                            resolved={human().resolved === row().item.id}
                          />
                        )}
                      </Index>
                    </div>
                  </div>

                  <Show when={chores() > 0}>
                    <div class="panel">
                      <div class="panel-h">
                        <h2>Housekeeping for the controller</h2>
                        <span class="n">{chores()}</span>
                        <button
                          type="button"
                          class="r"
                          aria-expanded={showHousekeeping()}
                          onClick={() => setShowHousekeeping(!showHousekeeping())}
                        >
                          {showHousekeeping() ? 'Hide' : `Show ${groups().length} ${plural(groups().length, 'group')}`}
                        </button>
                      </div>
                      <Show when={showHousekeeping()}>
                        <For each={groups()}>
                          {(group) => (
                            <Group
                              group={group}
                              asOf={box().asOf}
                              open={openGroups().has(group.key)}
                              toggle={() => toggle(group.key)}
                              selected={params.item}
                              href={rowHref}
                            />
                          )}
                        </For>
                      </Show>
                    </div>
                  </Show>
                </div>
              )
            }}
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
