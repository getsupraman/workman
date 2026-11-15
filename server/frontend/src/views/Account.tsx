import { For, Show, createSignal } from 'solid-js'
import * as api from '../api.ts'
import { account, load } from '../store.ts'
import { PASSWORD_MIN, type PasswordChange, passwordMessage, passwordProblem } from '../admin.ts'
import { type ConnectedApp, appLine, revokeWarning } from '../apps.ts'
import { type IconState, iconStep, initials } from '../connect.ts'
import { Confirm, Known, OnceLink } from '../ui.tsx'
import { day } from '../format.ts'

/** `/account` (doc 13): who is signed in, the apps that hold their access, and the password change. */
export default function Account() {
  const [current, setCurrent] = createSignal('')
  const [next, setNext] = createSignal('')
  const [repeat, setRepeat] = createSignal('')
  const [tried, setTried] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [result, setResult] = createSignal<PasswordChange>()

  const problem = () =>
    current() === '' ? 'Type your current password.' : (passwordProblem(next()) ?? (next() === repeat() ? null : 'The two new passwords differ.'))

  const submit = async (event: SubmitEvent) => {
    event.preventDefault()
    setTried(true)
    setResult(undefined)
    if (problem() !== null) return
    setBusy(true)
    try {
      const outcome = await api.changePassword(current(), next())
      setResult(outcome)
      if (outcome.k === 'ok') {
        setCurrent('')
        setNext('')
        setRepeat('')
        setTried(false)
      }
    } catch (failure) {
      if (!(failure instanceof api.Unauthorized)) {
        setResult({ k: 'refused', error: failure instanceof Error ? failure.message : String(failure) })
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="page">
      <div class="page-h">
        <div>
          <h1>Account</h1>
          <div class="sub">
            <Show when={account()} fallback="Signed in.">
              {(a) => (
                <>
                  Signed in as <strong>{a().user.name}</strong>
                  {a().user.systemAdmin ? ', a system administrator' : ''}
                  {a().tenants.find((t) => t.current) ? `, in ${a().tenants.find((t) => t.current)!.slug}` : ''}.
                </>
              )}
            </Show>
          </div>
        </div>
      </div>

      <ConnectedApps />

      <CalendarFeed />

      <form class="panel account-form" onSubmit={(event) => void submit(event)}>
        <div class="panel-h">
          <h2>Change password</h2>
        </div>
        <div class="admin-form">
          <label for="pw-current">Current password</label>
          <input id="pw-current" type="password" autocomplete="current-password" value={current()} onInput={(e) => setCurrent(e.currentTarget.value)} />
          <label for="pw-next">New password</label>
          <input id="pw-next" type="password" autocomplete="new-password" value={next()} onInput={(e) => setNext(e.currentTarget.value)} />
          <label for="pw-repeat">New password, again</label>
          <input id="pw-repeat" type="password" autocomplete="new-password" value={repeat()} onInput={(e) => setRepeat(e.currentTarget.value)} />
          <p class="admin-hint">
            At least {PASSWORD_MIN} characters. Changing it signs out your other sessions; this one stays, and so do the
            apps you connected with OAuth.
          </p>
          <button type="submit" class="proj-act primary" disabled={busy()}>
            {busy() ? 'Changing…' : 'Change password'}
          </button>
          <Show when={tried() && problem()}>{(text) => <div class="failure confirm-err">{text()}</div>}</Show>
          <Show when={result()}>
            {(r) => <div class={r().k === 'ok' ? 'done-note' : 'failure confirm-err'}>{passwordMessage(r())}</div>}
          </Show>
        </div>
      </form>
    </div>
  )
}

/** The user's OAuth approvals in this workspace, most recently used first, each with Revoke. */
function ConnectedApps() {
  const [tick, setTick] = createSignal(0)
  const page = load(() => String(tick()), () => api.connectedApps(), { live: false })

  return (
    <div class="panel account-apps">
      <div class="panel-h">
        <h2>Connected apps</h2>
      </div>
      <Show when={page.error()}>{(e) => <div class="failure">{e()}</div>}</Show>
      <Known value={page.data()}>
        {(p) => (
          <For each={p().apps} fallback={<div class="empty">No apps hold access to this workspace.</div>}>
            {(app) => <AppRow app={app} asOf={p().asOf} onRevoked={() => setTick((n) => n + 1)} />}
          </For>
        )}
      </Known>
    </div>
  )
}

function AppRow(props: { app: ConnectedApp; asOf: string; onRevoked: () => void }) {
  const [confirming, setConfirming] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()

  const revoke = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await api.revokeApp(props.app.id)
      setConfirming(false)
      props.onRevoked()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="row">
      <AppIcon clientId={props.app.clientId} name={props.app.name} />
      <div>
        <div class="row-t">{props.app.name}</div>
        <div class="row-m">{appLine(props.app, props.asOf)}</div>
        <Show when={confirming()}>
          <Confirm
            label={`Revoke ${props.app.name}?`}
            action="Revoke"
            busy={busy()}
            onConfirm={() => void revoke()}
            onCancel={() => setConfirming(false)}
            style={{ 'margin-top': '8px' }}
          >
            Revoke <strong>{props.app.name}</strong>? {revokeWarning(props.app)}
          </Confirm>
        </Show>
        <Show when={error()}>{(message) => <div class="failure confirm-err">Couldn't revoke it: {message()}</div>}</Show>
      </div>
      <button type="button" class="proj-act" disabled={busy() || confirming()} onClick={() => setConfirming(true)}>
        Revoke
      </button>
    </div>
  )
}

/**
 * The user's calendar feed (doc 13): whether one exists and since when; Create shows the URL once, with
 * Copy, because the server keeps only its hash; Replace makes a new one and ends the old; Revoke asks first.
 */
function CalendarFeed() {
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
    <div class="panel account-feed">
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

/** The consent page's icon for the client, or its initials when it declared none. */
function AppIcon(props: { clientId: string; name: string }) {
  const [icon, setIcon] = createSignal<IconState>(props.clientId ? 'loading' : 'initials')

  return (
    <span class="app-ic" aria-hidden="true">
      <Show when={props.clientId}>
        <img
          src={`/connect/app-icon?client_id=${encodeURIComponent(props.clientId)}`}
          alt=""
          hidden={icon() !== 'image'}
          onLoad={() => setIcon(iconStep(icon(), 'load'))}
          onError={() => setIcon(iconStep(icon(), 'error'))}
        />
      </Show>
      <Show when={icon() !== 'image'}>{initials(props.name)}</Show>
    </span>
  )
}
