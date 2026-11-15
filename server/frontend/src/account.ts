/**
 * The sidebar's account header (doc 13, Information architecture) as plain functions, so `verify/state-cases.mjs`
 * walks them without a browser: who is signed in and where, the live dot's words, and the theme choice.
 */
import { initials } from './connect.ts'
import { roleLabel } from './admin.ts'

export type HeaderFacts = { initials: string; name: string; workspace: string | null; role: string | null }

/** The avatar is the name's initials (no remote image); the workspace and role are the session's current tenant. */
export function headerFacts(name: string, tenants: { slug: string; role: string; current: boolean }[]): HeaderFacts {
  const here = tenants.find((t) => t.current)
  return { initials: initials(name), name, workspace: here?.slug ?? null, role: here ? roleLabel(here.role) : null }
}

export type LinkState = 'connecting' | 'live' | 'down'

/** The dot's colour, the words beside it (none while live) and its tooltip. `asOf` is the read's time, formatted. */
export type LiveLabel = { dot: LinkState; text: string | null; tip: string }

export function liveLabel(state: LinkState, asOf: string | null): LiveLabel {
  const data = asOf === null ? 'loading' : `data as of ${asOf}`
  if (state === 'live') return { dot: 'live', text: null, tip: `Live updates · ${data}` }
  const text = state === 'connecting' && asOf === null ? 'Connecting…' : 'Reconnecting…'
  return { dot: state, text, tip: `Live updates paused · ${data}` }
}

export const THEME_KEY = 'workman.theme'

/** System leaves nothing stored and `data-theme` unset, so the CSS follows `prefers-color-scheme`. */
export type ThemeChoice = 'system' | 'light' | 'dark'

export const THEME_CHOICES: readonly [ThemeChoice, string][] = [
  ['system', 'System'],
  ['light', 'Light'],
  ['dark', 'Dark'],
]

/** What a stored value means; anything else, or nothing, is System. */
export const themeFromStored = (value: string | null): ThemeChoice => (value === 'dark' || value === 'light' ? value : 'system')

/** What a choice stores: `null` removes the key. */
export const storedForTheme = (choice: ThemeChoice): string | null => (choice === 'system' ? null : choice)

/** The look showing under a choice, given whether the OS prefers dark. */
export const shownTheme = (choice: ThemeChoice, systemDark: boolean): 'light' | 'dark' =>
  choice === 'system' ? (systemDark ? 'dark' : 'light') : choice

/** The palette's Toggle theme: the opposite of what shows now, as an explicit choice. */
export const toggledTheme = (choice: ThemeChoice, systemDark: boolean): ThemeChoice =>
  shownTheme(choice, systemDark) === 'dark' ? 'light' : 'dark'

/** ↓/↑ (wrapping) and Home/End over a menu's items; `null` for any other key. */
export function menuStep(key: string, index: number, count: number): number | null {
  if (count === 0) return null
  if (key === 'ArrowDown' || key === 'ArrowRight') return index < 0 ? 0 : (index + 1) % count
  if (key === 'ArrowUp' || key === 'ArrowLeft') return index < 0 ? count - 1 : (index - 1 + count) % count
  if (key === 'Home') return 0
  if (key === 'End') return count - 1
  return null
}
