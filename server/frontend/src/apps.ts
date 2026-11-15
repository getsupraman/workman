import { ago, plural } from './format.ts'

/** `GET /api/session/apps` (doc 13): one OAuth approval of the signed-in user in this workspace. */
export type ConnectedApp = {
  id: string
  clientId: string
  name: string
  scopes: string[]
  createdAt: string | null
  lastUsedAt: string | null
  sessions: number
}

export type ConnectedApps = { asOf: string; apps: ConnectedApp[] }

export type RevokedApp = { tokensRevoked: number; sessionsRevoked: number }

export const sessionsLabel = (n: number) => `${n} agent ${plural(n, 'session')}`

/** `mcp offline_access · connected 3d ago · last used 5m ago · 1 agent session`, relative to the server's `asOf`. */
export function appLine(app: ConnectedApp, asOf: string): string {
  const parts = [app.scopes.join(' ')]
  if (app.createdAt) parts.push(`connected ${ago(app.createdAt, asOf)}`)
  parts.push(app.lastUsedAt ? `last used ${ago(app.lastUsedAt, asOf)}` : 'not used yet')
  parts.push(sessionsLabel(app.sessions))
  return parts.filter(Boolean).join(' · ')
}

/** What Revoke ends, in the confirm's words. */
export const revokeWarning = (app: ConnectedApp) =>
  app.sessions === 0
    ? `${app.name} will need to sign in again.`
    : app.sessions === 1
      ? `${app.name} will need to sign in again, and its agent session stops.`
      : `${app.name} will need to sign in again, and its ${sessionsLabel(app.sessions)} stop.`

/**
 * One app on the Account page: every connection that carries its name. Each sign-in registers its own OAuth
 * client, so the client id differs per connection and the name is the key (trimmed, case-insensitive).
 */
export type AppGroup = {
  key: string
  name: string
  /** The latest-used connection's client, for the icon. */
  clientId: string
  /** Latest used first. */
  connections: ConnectedApp[]
  /** Every scope any connection holds, in the order first seen. */
  scopes: string[]
  sessions: number
  lastUsedAt: string | null
}

export const appKey = (name: string) => name.trim().toLowerCase()

const time = (iso: string | null) => (iso === null ? Number.NEGATIVE_INFINITY : Date.parse(iso))

/** Latest used first, never-used last; then the newest connection; then the name. */
const byUse = (a: { lastUsedAt: string | null; createdAt?: string | null; name: string }, b: typeof a) =>
  time(b.lastUsedAt) - time(a.lastUsedAt) || time(b.createdAt ?? null) - time(a.createdAt ?? null) || a.name.localeCompare(b.name)

export function groupApps(apps: ConnectedApp[]): AppGroup[] {
  const byKey = new Map<string, ConnectedApp[]>()
  for (const app of apps) {
    const key = appKey(app.name)
    byKey.set(key, [...(byKey.get(key) ?? []), app])
  }
  const groups = [...byKey].map(([key, list]): AppGroup => {
    const connections = [...list].sort(byUse)
    const latest = connections[0]!
    return {
      key,
      name: latest.name.trim(),
      clientId: latest.clientId,
      connections,
      scopes: [...new Set(connections.flatMap((c) => c.scopes))],
      sessions: connections.reduce((n, c) => n + c.sessions, 0),
      lastUsedAt: latest.lastUsedAt,
    }
  })
  return groups.sort(byUse)
}

/** `2 connections · last used 5m ago · 3 agent sessions`, relative to the server's `asOf`. */
export function groupLine(group: AppGroup, asOf: string): string {
  const n = group.connections.length
  return [
    `${n} ${plural(n, 'connection')}`,
    group.lastUsedAt ? `last used ${ago(group.lastUsedAt, asOf)}` : 'not used yet',
    sessionsLabel(group.sessions),
  ].join(' · ')
}

/** One connection under an expanded app: `connected 3d ago · last used 5m ago · 1 agent session`. */
export function connectionLine(app: ConnectedApp, asOf: string): string {
  const parts: string[] = []
  if (app.createdAt) parts.push(`connected ${ago(app.createdAt, asOf)}`)
  parts.push(app.lastUsedAt ? `last used ${ago(app.lastUsedAt, asOf)}` : 'not used yet')
  parts.push(sessionsLabel(app.sessions))
  return parts.join(' · ')
}

/** The Account page's calendar line; the URL itself is managed on the Agenda page. */
export const feedStatus = (feed: { exists: boolean; createdAt?: string | null }, day: (iso: string) => string) =>
  feed.exists ? `You have a calendar URL${feed.createdAt ? `, created ${day(feed.createdAt)}` : ''}.` : 'No calendar URL yet.'
