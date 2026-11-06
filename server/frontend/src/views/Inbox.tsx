import { For, Index, Show, createSignal } from 'solid-js'
import { A } from '@solidjs/router'
import type { InboxRow } from '../api.ts'
import { inbox } from '../store.ts'
import { cardFacts, groupHousekeeping, groupInbox, inboxKind, needsYou, type HousekeepingGroup } from '../inbox.ts'
import { itemHref } from '../nav.ts'
import { ago, plural } from '../format.ts'
import { CopyRef, Dot, Known } from '../ui.tsx'

export function InboxRowView(props: { row: InboxRow; asOf: string }) {
  const r = () => props.row
  const kind = () => inboxKind(r().kind)

  return (
    <A class="row" href={itemHref(r().item, r().run)}>
      <Dot k={kind().dot} />
      <div>
        <div class="row-t">{r().item.title}</div>
        <div class="row-m">
          {kind().label} · {r().item.qualifiedRef} <CopyRef item={r().item} />
          <For each={cardFacts(r())}>{(fact) => <>· {fact} </>}</For>· {ago(r().since, props.asOf)}
        </div>
        <Show when={r().detail}>{(detail) => <div class="row-d">{detail()}</div>}</Show>
      </div>
      <span />
    </A>
  )
}

/** One housekeeping group. Open state lives with the page, keyed by group, so a refresh keeps it. */
function Group(props: { group: HousekeepingGroup; asOf: string; open: boolean; toggle: () => void }) {
  return (
    <>
      <button type="button" class="row-more runs-more" aria-expanded={props.open} onClick={() => props.toggle()}>
        {props.open ? '▾' : '▸'} {props.group.label}
      </button>
      <Show when={props.open}>
        <Index each={props.group.rows}>{(row) => <InboxRowView row={row()} asOf={props.asOf} />}</Index>
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
