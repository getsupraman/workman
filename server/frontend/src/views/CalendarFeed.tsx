import { Show, createSignal } from 'solid-js'
import * as api from '../api.ts'
import { load } from '../store.ts'
import { Confirm, Known, OnceLink } from '../ui.tsx'
import { day } from '../format.ts'

/**
 * The user's calendar feed (doc 13): whether one exists and since when; Create shows the URL once, with
 * Copy, because the server keeps only its hash; Replace makes a new one and ends the old; Revoke asks first.
 */
export default function CalendarFeed() {
  const [tick, setTick] = createSignal(0)
  const feed = load(() => String(tick()), () => api.calendarFeed(), { live: false })
  const [created, setCreated] = createSignal<api.CreatedFeed>()
  const [confirming, setConfirming] = createSignal<'replace' | 'revoke'>()
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()

  const act = async (write: () => Promise<void>) => {
    setBusy(true)
    setError(undefined)
    try {
      await write()
      setConfirming(undefined)
      setTick((n) => n + 1)
    } catch (failure) {
      if (!(failure instanceof api.Unauthorized)) setError(failure instanceof Error ? failure.message : String(failure))
    } finally {
      setBusy(false)
    }
  }
  const create = () =>
    act(async () => {
      setCreated(await api.createCalendarFeed())
    })
  const revoke = () =>
    act(async () => {
      setCreated(undefined)
      await api.revokeCalendarFeed()
    })

  return (
    <div class="panel calendar-feed">
      <div class="panel-h">
        <h2>Calendar feed</h2>
      </div>
      <Show when={feed.error()}>{(e) => <div class="failure">{e()}</div>}</Show>
      <Known value={feed.data()}>
        {(f) => (
          <div class="feed-row">
            <Show
              when={f().exists}
              fallback={
                <>
                  <button type="button" class="proj-act primary" disabled={busy()} onClick={() => void create()}>
                    {busy() ? 'Creating…' : 'Create calendar URL'}
                  </button>
                  <p>
                    A private URL your calendar app subscribes to: the due dates of the open items you can see in this
                    workspace, one event each. You have none yet.
                  </p>
                </>
              }
            >
              <span>
                You have a calendar URL{f().createdAt ? `, created ${day(f().createdAt!)}` : ''}.
              </span>
              <button type="button" class="proj-act" disabled={busy() || confirming() !== undefined} onClick={() => setConfirming('replace')}>
                Replace
              </button>
              <button type="button" class="proj-act" disabled={busy() || confirming() !== undefined} onClick={() => setConfirming('revoke')}>
                Revoke
              </button>
              <p>It is shown only when it is made. Lost it? Replace it: the new URL works and the old one stops.</p>
            </Show>
            <Show when={confirming()}>
              {(what) => (
                <Confirm
                  label={what() === 'replace' ? 'Replace the calendar URL?' : 'Revoke the calendar URL?'}
                  action={what() === 'replace' ? 'Replace' : 'Revoke'}
                  busy={busy()}
                  onConfirm={() => void (what() === 'replace' ? create() : revoke())}
                  onCancel={() => setConfirming(undefined)}
                  style={{ 'flex-basis': '100%' }}
                >
                  {what() === 'replace'
                    ? 'Make a new URL? The one your calendar app holds stops working at once; subscribe again with the new one.'
                    : 'Revoke the URL? Your calendar app stops getting the dates at its next refresh.'}
                </Confirm>
              )}
            </Show>
            <Show when={error()}>{(message) => <div class="failure confirm-err">{message()}</div>}</Show>
          </div>
        )}
      </Known>
      <Show when={created()}>
        {(c) => (
          <OnceLink url={c().url} onDismiss={() => setCreated(undefined)}>
            Copy it now and subscribe to it in your calendar app: it is shown once, and the server keeps only a hash of it.
            The URL is a secret — anyone who has it can read the due dates you can see, until you replace or revoke it.
          </OnceLink>
        )}
      </Show>
    </div>
  )
}
