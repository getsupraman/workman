/**
 * Every key the dashboard takes, as one table: the `?` help dialog renders it, and doc 13's Keyboard
 * section is `keysTable()` verbatim (`verify/state-cases.mjs` holds the two together). Pure.
 */
import type { Command } from './palette.ts'
import type { Key } from './listnav.ts'

export type KeyHelp = { keys: string; where: string; does: string }

export const KEYS: readonly KeyHelp[] = [
  { keys: 'Ctrl+P (⌘P on macOS), /', where: 'any page', does: 'opens the command palette; / only outside a text field' },
  { keys: '↓ / ↑', where: 'run page, Needs you', does: 'selects the next / previous row as drawn and shows it in the detail panel' },
  { keys: '↓ / ↑', where: 'Backlog, Project, Activity', does: 'moves focus to the next / previous row' },
  { keys: 'Home / End', where: 'run page, Needs you, Backlog, Project, Activity', does: 'the first / last row' },
  { keys: 'Enter', where: 'a focused row', does: 'opens it' },
  { keys: 'Alt+↓ / Alt+↑ (⌥ on macOS)', where: 'any page', does: 'opens the next / previous run in the sidebar' },
  { keys: 'Esc', where: 'palette, dialog, phone menu, detail sheet', does: 'closes it' },
  { keys: '?', where: 'any page', does: 'shows these keys' },
]

/** The table as doc 13 prints it. */
export const keysTable = (): string =>
  ['| Key | Where | What it does |', '|---|---|---|', ...KEYS.map((k) => `| ${k.keys} | ${k.where} | ${k.does} |`)].join('\n')

/** `?` opens the help, outside a field and with no dialog open; Shift is how most layouts type it. */
export const isHelpKey = (e: Key & { defaultPrevented?: boolean }, typing: boolean, modal: boolean): boolean =>
  e.key === '?' && !typing && !modal && !e.defaultPrevented && !e.ctrlKey && !e.metaKey && !e.altKey

/** The palette's way to the same dialog. */
export const KEYS_COMMAND: Command = {
  id: 'keys',
  label: 'Keyboard shortcuts',
  keywords: 'keys help shortcuts ?',
  href: null,
  action: 'keys',
}
