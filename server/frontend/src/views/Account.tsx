import { For, Show, createSignal } from 'solid-js'
import { A } from '@solidjs/router'
import * as api from '../api.ts'
import { account, load } from '../store.ts'
import { PASSWORD_MIN, type PasswordChange, passwordMessage, passwordProblem } from '../admin.ts'
import { type AppGroup, type ConnectedApp, connectionLine, feedStatus, groupApps, groupLine, revokeWarning } from '../apps.ts'
import { headerFacts } from '../account.ts'
import { type IconState, iconStep, initials } from '../connect.ts'
import { Confirm, Known } from '../ui.tsx'
import { day } from '../format.ts'
import { agendaHref } from '../nav.ts'

/** `/account` (doc 13): one even column — who is signed in, the apps holding their access, the calendar URL's status, the password. */
export default function Account() {
  return (
    <div class="page">
      <div class="account-col">
        <Profile />
        <ConnectedApps />
        <FeedStatus />
        <Security />
      </div>
    </div>
  )
}

/** The large initials avatar, the sign-in name, the workspace with the role, and Sign out. */
function Profile() {
  const facts = () => {
    const a = account()
    return a ? headerFacts(a.user.name, a.tenants) : null
  }

  return (
    <div class="panel account-profile">
      <span class="account-av" aria-hidden="true">
        {facts()?.initials ?? '?'}
      </span>
      <div class="account-who">
        <h1>{facts()?.name ?? 'Account'}</h1>
        <div class="account-where">
          <Show when={facts()?.workspace}>{(ws) => <span>{ws()}</span>}</Show>
          <Show when={facts()?.role}>{(role) => <span class="account-role">{role()}</span>}</Show>
          <Show when={account()?.user.systemAdmin}>
            <span class="account-role">System administrator</span>
          </Show>
        </div>
      </div>
      <button type="button" class="proj-act" onClick={() => void api.signOut()}>
        Sign out
      </button>
    </div>
  )
}

/** The user's OAuth approvals in this workspace, one row per app (most recently used first). */
function ConnectedApps() {
  const [tick, setTick] = createSignal(0)
  const page = load(() => String(tick()), () => api.connectedApps(), { live: false })
  const reload = () => setTick((n) => n + 1)

  return (
    <div class="panel account-apps">
      <div class="panel-h">
        <h2>Connected apps</h2>
      </div>
      <Show when={page.error()}>{(e) => <div class="failure">{e()}</div>}</Show>
      <Known value={page.data()}>
        {(p) => (
          <For each={groupApps(p().apps)} fallback={<div class="empty">No apps hold access to this workspace.</div>}>
            {(group) => <AppGroupRow group={group} asOf={p().asOf} onRevoked={reload} />}
          </For>
        )}
      </Known>
    </div>
  )
}

/** Revoking one connection: ask first, then end it and reload the list. */
function useRevoke(app: () => ConnectedApp, onRevoked: () => void) {
  const [confirming, setConfirming] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [error, setError] = createSignal<string>()

  const revoke = async () => {
    setBusy(true)
    setError(undefined)
    try {
      await api.revokeApp(app().id)
      setConfirming(false)
      onRevoked()
    } catch (failure) {
      setError(failure instanceof Error ? failure.message : String(failure))
    } finally {
      setBusy(false)
    }
  }

  return { app, confirming, setConfirming, busy, error, revoke }
}

type Revoking = ReturnType<typeof useRevoke>

function RevokeButton(props: { r: Revoking }) {
  return (
    <button type="button" class="proj-act" disabled={props.r.busy() || props.r.confirming()} onClick={() => props.r.setConfirming(true)}>
      Revoke
    </button>
  )
}

function RevokeConfirm(props: { r: Revoking }) {
  return (
    <>
      <Show when={props.r.confirming()}>
        <Confirm
          label={`Revoke ${props.r.app().name}?`}
          action="Revoke"
          busy={props.r.busy()}
          onConfirm={() => void props.r.revoke()}
          onCancel={() => props.r.setConfirming(false)}
          style={{ 'margin-top': '8px' }}
        >
          Revoke <strong>{props.r.app().name}</strong>? {revokeWarning(props.r.app())}
        </Confirm>
      </Show>
      <Show when={props.r.error()}>{(message) => <div class="failure confirm-err">Couldn't revoke it: {message()}</div>}</Show>
    </>
  )
}

/** One app: its line and scope chips; one connection is revoked on the row, several expand to a line each. */
function AppGroupRow(props: { group: AppGroup; asOf: string; onRevoked: () => void }) {
  const [open, setOpen] = createSignal(false)
  const single = () => props.group.connections.length === 1
  const only = useRevoke(() => props.group.connections[0]!, () => props.onRevoked())
  const listId = () => `app-conns-${props.group.connections[0]!.id}`

  return (
    <div class="app-group">
      <div class="row">
        <AppIcon clientId={props.group.clientId} name={props.group.name} />
        <div>
          <div class="row-t">{props.group.name}</div>
          <div class="row-m">{groupLine(props.group, props.asOf)}</div>
          <Show when={props.group.scopes.length > 0}>
            <div class="app-scopes">
              <For each={props.group.scopes}>{(scope) => <span class="app-scope">{scope}</span>}</For>
            </div>
          </Show>
          <Show when={single()}>
            <RevokeConfirm r={only} />
          </Show>
        </div>
        <Show
          when={single()}
          fallback={
            <button type="button" class="proj-act" aria-expanded={open()} aria-controls={listId()} onClick={() => setOpen(!open())}>
              {open() ? 'Hide' : 'Show'} {props.group.connections.length} connections
            </button>
          }
        >
          <RevokeButton r={only} />
        </Show>
      </div>
      <Show when={!single() && open()}>
        <div class="app-conns" id={listId()}>
          <For each={props.group.connections}>{(app) => <ConnectionRow app={app} asOf={props.asOf} onRevoked={props.onRevoked} />}</For>
        </div>
      </Show>
    </div>
  )
}

function ConnectionRow(props: { app: ConnectedApp; asOf: string; onRevoked: () => void }) {
  const r = useRevoke(() => props.app, () => props.onRevoked())

  return (
    <div class="app-conn">
      <div>
        <div class="row-m">{connectionLine(props.app, props.asOf)}</div>
        <RevokeConfirm r={r} />
      </div>
      <RevokeButton r={r} />
    </div>
  )
}

/** Whether the user has a calendar URL; creating, replacing and revoking it live on the Agenda page. */
function FeedStatus() {
  const feed = load(() => 'feed', () => api.calendarFeed(), { live: false })

  return (
    <div class="panel">
      <div class="panel-h">
        <h2>Calendar feed</h2>
      </div>
      <div class="account-line">
        <Show when={feed.error()} fallback={<span>{feed.data() ? feedStatus(feed.data()!, day) : 'Loading…'}</span>}>
          {(e) => <span class="failure">{e()}</span>}
        </Show>
        <A href={agendaHref()} class="linkish">
          Manage on the Agenda page
        </A>
      </div>
    </div>
  )
}

/** The password, its form behind a button; the rules and messages are the form's own. */
function Security() {
  const [editing, setEditing] = createSignal(false)
  const [current, setCurrent] = createSignal('')
  const [next, setNext] = createSignal('')
  const [repeat, setRepeat] = createSignal('')
  const [tried, setTried] = createSignal(false)
  const [busy, setBusy] = createSignal(false)
  const [result, setResult] = createSignal<PasswordChange>()

  const problem = () =>
    current() === '' ? 'Type your current password.' : (passwordProblem(next()) ?? (next() === repeat() ? null : 'The two new passwords differ.'))

  const clear = () => {
    setCurrent('')
    setNext('')
    setRepeat('')
    setTried(false)
  }
  const cancel = () => {
    clear()
    setResult(undefined)
    setEditing(false)
  }

  const submit = async (event: SubmitEvent) => {
    event.preventDefault()
    setTried(true)
    setResult(undefined)
    if (problem() !== null) return
    setBusy(true)
    try {
      const outcome = await api.changePassword(current(), next())
      setResult(outcome)
      if (outcome.k === 'ok') clear()
    } catch (failure) {
      if (!(failure instanceof api.Unauthorized)) {
        setResult({ k: 'refused', error: failure instanceof Error ? failure.message : String(failure) })
      }
    } finally {
      setBusy(false)
    }
  }

  return (
    <div class="panel">
      <div class="panel-h">
        <h2>Security</h2>
      </div>
      <div class="account-line">
        <span>Password</span>
        <Show when={!editing()}>
          <button type="button" class="proj-act" onClick={() => setEditing(true)}>
            Change password
          </button>
        </Show>
      </div>
      <Show when={editing()}>
        <form class="account-form" onSubmit={(event) => void submit(event)}>
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
            <div class="account-acts">
              <button type="submit" class="proj-act primary" disabled={busy()}>
                {busy() ? 'Changing…' : 'Change password'}
              </button>
              <button type="button" class="proj-act" disabled={busy()} onClick={cancel}>
                Cancel
              </button>
            </div>
            <Show when={tried() && problem()}>{(text) => <div class="failure confirm-err">{text()}</div>}</Show>
            <Show when={result()}>
              {(r) => <div class={r().k === 'ok' ? 'done-note' : 'failure confirm-err'}>{passwordMessage(r())}</div>}
            </Show>
          </div>
        </form>
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
