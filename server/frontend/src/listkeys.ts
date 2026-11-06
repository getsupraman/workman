import { onCleanup, onMount } from 'solid-js'
import { listMove, step, typingIn, type Key, type Move } from './listnav.ts'

/** A dialog is open (the palette, the key help) or the phone menu covers the page: list keys stand down. */
export const modalOpen = (): boolean =>
  document.querySelector('[aria-modal="true"]') !== null || document.querySelector('.app.menu-open') !== null

/**
 * Listens on the document in the bubble phase, so the palette's window capture and the shell's
 * Escape run first. `onKey` returns whether it took the key; a taken key does not scroll the page.
 */
export function useDocumentKeys(onKey: (e: KeyboardEvent & Key, typing: boolean, modal: boolean) => boolean): void {
  onMount(() => {
    const key = (e: KeyboardEvent) => {
      if (onKey(e, typingIn(e.target), modalOpen())) e.preventDefault()
    }
    document.addEventListener('keydown', key)
    onCleanup(() => document.removeEventListener('keydown', key))
  })
}

/** ArrowUp/ArrowDown, Home/End on a list page; `move` returns whether the list took the key. */
export function useListKeys(move: (m: Move) => boolean): void {
  useDocumentKeys((e, typing, modal) => {
    const m = listMove(e, typing, modal)
    return m !== null && move(m)
  })
}

/**
 * A list page with no side panel: ArrowUp/ArrowDown, Home/End move focus along the row links tagged
 * `data-row`, in `order` (only the rows drawn now count); Enter on the focused link opens it.
 */
export function useFocusKeys(order: () => string[]): void {
  const row = (key: string) => document.querySelector<HTMLElement>(`[data-row="${CSS.escape(key)}"]`)
  useListKeys((m) => {
    const keys = order().filter((k) => row(k) !== null)
    const current = document.activeElement?.closest<HTMLElement>('[data-row]')?.dataset.row
    const to = step(keys, current, m)
    const el = to === undefined ? null : row(to)
    if (el === null) return false
    el.focus({ preventScroll: true })
    el.scrollIntoView({ block: 'nearest' })
    return true
  })
}

/** After a keyed move the new row is in the DOM: bring it into view without jumping the page. */
export function reveal(selector: string): void {
  requestAnimationFrame(() => document.querySelector(selector)?.scrollIntoView({ block: 'nearest' }))
}
