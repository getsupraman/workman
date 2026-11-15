import { createEffect, createSignal, on, onCleanup, type Accessor } from 'solid-js'
import * as api from './api.ts'
import type { SessionTenant } from './connect.ts'
import { boardsRead } from './boards.ts'
import { onFrames } from './live.ts'
import { DEFAULT_ZONE, zonedDay } from './dates.ts'

/**
 * The workspace as the shell reads it: the overview, the inbox, and every run's bars (the sidebar's
 * and the overview's segmented bars draw one segment per leaf, which `/bars` carries without the
 * board behind it). `undefined` means not asked yet — never "none" — so no panel can say "nothing"
 * before it knows.
 */
const [overview, setOverview] = createSignal<api.Overview>()
const [inbox, setInbox] = createSignal<api.Inbox>()
const [bars, setBars] = createSignal<Record<string, api.RunBars>>({})
const [failure, setFailure] = createSignal<string>()

export { overview, inbox, bars, failure }

/** Bumped once per burst of frames: the page-level reads (an item, the journal) key on it. */
const [version, setVersion] = createSignal(0)

const note = (error: unknown) => {
  if (error instanceof api.Unauthorized) return
  setFailure(error instanceof Error ? error.message : String(error))
}

async function loadOverview(runs: 'all' | string[]): Promise<void> {
  try {
    const next = await api.overview()
    setOverview(next)
    setFailure(undefined)

    const read = boardsRead(next, runs, bars())
    if (read === null) return
    const fresh = await api.bars(read === 'all' ? undefined : read)
    setBars((all) => ({ ...all, ...Object.fromEntries(fresh.runs.map((b) => [b.run, b])) }))
  } catch (error) {
    note(error)
  }
}

/**
 * Who is signed in and which workspaces they may enter: the sidebar's Admin link, its tenant switcher and the
 * palette's commands. Not live: the journal's member_added / member_removed are a project's members, which the
 * overview and the Members panel re-read on their frame, and say nothing about a workspace's — so an admin write
 * that may change the caller's own rights asks again with `refreshAccount`.
 */
export type Account = { user: api.SessionUser; tenants: SessionTenant[] }

const [account, setAccount] = createSignal<Account>()
export { account }

export async function refreshAccount(): Promise<void> {
  try {
    const [user, { tenants }] = await Promise.all([api.me(), api.sessionTenants()])
    setAccount({ user, tenants: tenants ?? [] })
  } catch (error) {
    note(error)
  }
}

/** After a write: the journal frame would bring the same re-read, this one does not wait for it. */
export const refreshOverview = (): Promise<void> => loadOverview([])

async function loadInbox(): Promise<void> {
  try {
    setInbox(await api.inbox())
  } catch (error) {
    note(error)
  }
}

/**
 * The workspace's time zone (IANA), in which a date-only due is a whole day and an instant is shown
 * (doc 13 "Dates"). Every member may read the agenda, which names it; only an admin may read the
 * workspace's settings, so the zone comes from there. `undefined` until it has answered.
 */
const [zone, setZone] = createSignal<string>()

/** The zone dates are drawn in: the workspace's once known, UTC before. */
export const workspaceZone = (): string => zone() ?? DEFAULT_ZONE

/** Any read that names the zone (the Agenda's, a settings write) keeps it current. */
export const noteZone = (value: string | null | undefined): void => {
  if (value) setZone(value)
}

async function loadZone(): Promise<void> {
  try {
    noteZone((await api.agenda(zonedDay(Date.now(), DEFAULT_ZONE))).zone)
  } catch {
    // A server without the agenda: dates read in UTC, as the server's own default.
  }
}

/** Opens the store and keeps it current until the owner is disposed. */
export function startStore(): void {
  void loadOverview('all')
  void loadInbox()
  void refreshAccount()
  void loadZone()

  onCleanup(
    onFrames((frames) => {
      const runs = frames.map((f) => f.runId)
      void loadOverview(runs.includes(null) ? 'all' : [...new Set(runs as string[])])
      void loadInbox()
      setVersion((n) => n + 1)
    }),
  )
}

type Load<T> = { data: Accessor<T | undefined>; missing: Accessor<boolean>; error: Accessor<string | undefined> }

/**
 * One read that follows `key` and, unless `live` is off, every live burst. The previous answer
 * stays on screen while a re-read is in flight; an answer for a key no longer current is dropped.
 */
export function load<T>(
  key: Accessor<string | undefined>,
  fetcher: (key: string) => Promise<T>,
  options: { live: boolean } = { live: true },
): Load<T> {
  const [data, setData] = createSignal<T>()
  const [missing, setMissing] = createSignal(false)
  const [error, setError] = createSignal<string>()
  let held: string | undefined
  let ticket = 0

  const run = (k: string | undefined) => {
    const mine = ++ticket
    if (k !== held) {
      held = k
      setData(undefined)
      setMissing(false)
      setError(undefined)
    }
    if (k === undefined) return

    fetcher(k).then(
      (value) => {
        if (mine !== ticket) return
        setData(() => value)
        setMissing(false)
        setError(undefined)
      },
      (failure: unknown) => {
        if (mine !== ticket || failure instanceof api.Unauthorized) return
        if (failure instanceof api.NotFound) setMissing(true)
        else setError(failure instanceof Error ? failure.message : String(failure))
      },
    )
  }

  createEffect(on(() => [key(), options.live ? version() : 0] as const, ([k]) => run(k)))

  return { data, missing, error }
}
