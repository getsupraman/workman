import { For, Index, Show, createSignal } from 'solid-js'
import { A } from '@solidjs/router'
import type { InboxRow } from '../api.ts'
import { inbox, overview } from '../store.ts'
import { choreRow, decisionRow, groupHousekeeping, groupInbox, inboxKind, needsYou, type HousekeepingGroup } from '../inbox.ts'
import { itemHref } from '../nav.ts'
import { ago, plural } from '../format.ts'
import { CopyRef, Dot, Known } from '../ui.tsx'

/**
 * One decision row, in the run plan list's language (`.item`, `.on`): the title first, the kind and
 * full ref muted under it, then the server's detail line. `compact` drops the detail (the overview).
 */
export function InboxRowView(props: { row: InboxRow; asOf: string; href?: string; selected?: boolean; compact?: boolean }) {
  const m = () => decisionRow(props.row, overview()?.projects)

  return (
    <A
      class={`item inbox-row${props.selected ? ' on' : ''}`}
      href={props.href ?? itemHref(props.row.item, props.row.run)}
      noScroll
      aria-current={props.selected ? 'page' : undefined}
    >
      <Dot k={m().dot} />
      <div class="inbox-b">
        <div class="inbox-t">{m().title}</div>
        <div class="inbox-m">
          {m().kind} · <span class="inbox-ref">{m().fullRef}</span>
          <CopyRef item={props.row.item} />
          <For each={m().facts}>{(fact) => <>· {fact} </>}</For>
        </div>
        <Show when={!props.compact && m().detail}>{(detail) => <div class="inbox-d">{detail()}</div>}</Show>
      </div>
      <div class="item-side" title={new Date(m().since).toLocaleString()}>
        {ago(m().since, props.asOf)}
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
      <div class="chore-t">
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
function Group(props: { group: HousekeepingGroup; asOf: string; open: boolean; toggle: () => void }) {
  return (
    <>
      <button type="button" class="item chore-group" aria-expanded={props.open} onClick={() => props.toggle()}>
        <Dot k={inboxKind(props.group.kind).dot} />
        <span class="item-t">{props.group.line}</span>
        <span class="item-side">{props.open ? '▾' : '▸'}</span>
      </button>
      <Show when={props.open}>
        <Index each={props.group.rows}>{(row) => <ChoreRowView row={row()} asOf={props.asOf} />}</Index>
      </Show>
    </>
  )
}

export default function Inbox() {
  const [showHousekeeping, setShowHousekeeping] = createSignal(false)
  const [openGroups, setOpenGroups] = createSignal<ReadonlySet<string>>(new Set())
  const toggle = (key: string) => {
    const next = new Set(openGroups())
    if (!next.delete(key)) next.add(key)
    setOpenGroups(next)
  }

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <h1>Needs you</h1>
          <div class="sub">The questions and blocked work that wait on a decision from you, newest first.</div>
        </div>
      </div>

      <Known value={inbox()} class="panel empty">
        {(box) => {
          const human = () => needsYou(box().rows)
          const groups = () => groupHousekeeping(box().rows)
          const chores = () => groups().reduce((n, g) => n + g.rows.length, 0)

          return (
            <div class="stack">
              <div>
                <Show when={human().length > 0}>
                  <div class="summary">
                    <For each={groupInbox(human())}>
                      {(group) => (
                        <span class="chip">
                          {group.label} <b>{group.rows.length}</b>
                        </span>
                      )}
                    </For>
                  </div>
                </Show>
                <div class="panel">
                  <Index each={human()} fallback={<div class="empty">Nothing needs you.</div>}>
                    {(row) => <InboxRowView row={row()} asOf={box().asOf} />}
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
                    <For each={groups()}>{(group) => (
                        <Group group={group} asOf={box().asOf} open={openGroups().has(group.key)} toggle={() => toggle(group.key)} />
                      )}</For>
                  </Show>
                </div>
              </Show>
            </div>
          )
        }}
      </Known>
    </div>
  )
}
