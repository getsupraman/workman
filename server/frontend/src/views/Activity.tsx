import { For, Show } from 'solid-js'
import { A } from '@solidjs/router'
import * as api from '../api.ts'
import type { JournalEvent } from '../api.ts'
import { load } from '../store.ts'
import { itemHref } from '../nav.ts'
import { clock } from '../format.ts'
import { activityDays, activityOrder, eventRow } from '../lists.ts'
import { useFocusKeys } from '../listkeys.ts'
import { CopyRef, Known } from '../ui.tsx'

const LIMIT = 300

export function EventLine(props: { event: JournalEvent; withRef: boolean }) {
  const e = () => props.event
  return (
    <div class="ev-row">
      <time>{clock(e().at)}</time>
      <div>
        <span class="k">{e().kind}</span>
        <Show when={props.withRef && e().item}>
          {(item) => (
            <>
              <A class="item-ref" href={itemHref(item(), e().run)} data-row={eventRow(e())}>
                {item().qualifiedRef}
              </A>
              <CopyRef item={item()} />
            </>
          )}
        </Show>
        {e().summary ?? ''} <span class="row-m">· {e().session?.name ?? e().actor}</span>
      </div>
    </div>
  )
}

export default function Activity() {
  const page = load(() => 'all', () => api.journal(LIMIT))
  const days = () => activityDays(page.data()?.events ?? [])
  useFocusKeys(() => activityOrder(days()))

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <h1>Activity</h1>
          <div class="sub">
            <Show when={page.data()} fallback="Loading…">
              {(p) =>
                `The last ${p().events.length} events across every project.${p().hasMore ? ' Older events are on each item’s Activity tab.' : ''}`
              }
            </Show>
          </div>
        </div>
      </div>

      <Show when={page.error()}>{(e) => <div class="failure">{e()}</div>}</Show>

      <Known value={page.data()} class="panel empty">
        {() => (
          <div class="panel journal" style={{ 'padding-bottom': '8px' }}>
            <For each={days()} fallback={<div class="empty">Nothing has happened yet.</div>}>
              {(group) => (
                <>
                  <div class="group-h">{group.day}</div>
                  <For each={group.events}>{(event) => <EventLine event={event} withRef />}</For>
                </>
              )}
            </For>
          </div>
        )}
      </Known>
    </div>
  )
}
