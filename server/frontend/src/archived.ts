import { createSignal } from 'solid-js'

const KEY = 'workman.archived-open'

/** Storage can throw outright (a private window, blocked site data); the toggle then lasts the page. */
function storedOpen(): boolean {
  try {
    return localStorage.getItem(KEY) === '1'
  } catch {
    return false
  }
}

/** Whether the sidebar's Archived group is expanded, per viewer. */
const [archivedOpen, setArchivedOpenSignal] = createSignal(storedOpen())
export { archivedOpen }

export function setArchivedOpen(open: boolean): void {
  setArchivedOpenSignal(open)
  try {
    localStorage.setItem(KEY, open ? '1' : '0')
  } catch {
    // remembered for this page only
  }
}
