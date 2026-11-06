import { For, Show, createEffect, createSignal, on, onCleanup, onMount } from 'solid-js'
import { KEYS, isHelpKey } from './keys.ts'
import { useDocumentKeys } from './listkeys.ts'

const [open, setOpen] = createSignal(false)

/** Opens the keyboard help from anywhere; the palette's Keyboard shortcuts command calls this. */
export const openKeyHelp = () => setOpen(true)

/**
 * The `?` dialog (doc 13, Keyboard): the keys from `src/keys.ts`. Modal; Esc or Close shuts it and
 * returns focus where it was. Mounted once, by the shell.
 */
export default function KeyHelp() {
  let close: HTMLButtonElement | undefined
  let returnTo: HTMLElement | null = null

  useDocumentKeys((e, typing, modal) => {
    if (!isHelpKey(e, typing, modal)) return false
    setOpen(true)
    return true
  })

  // Captured on the window, like the palette's, so a run sheet's own Escape never closes under it.
  onMount(() => {
    const key = (e: KeyboardEvent) => {
      if (open() && e.key === 'Escape') {
        e.preventDefault()
        e.stopPropagation()
        setOpen(false)
      }
    }
    window.addEventListener('keydown', key, true)
    onCleanup(() => window.removeEventListener('keydown', key, true))
  })

  createEffect(
    on(open, (isOpen) => {
      if (isOpen) {
        returnTo = document.activeElement instanceof HTMLElement ? document.activeElement : null
        queueMicrotask(() => close?.focus())
        return
      }
      if (returnTo?.isConnected) returnTo.focus()
      returnTo = null
    }, { defer: true }),
  )

  return (
    <Show when={open()}>
      <div class="pal-back" onMouseDown={(e) => e.target === e.currentTarget && setOpen(false)}>
        <div
          class="pal keyhelp"
          role="dialog"
          aria-modal="true"
          aria-labelledby="keyhelp-title"
          onKeyDown={(e) => {
            // Close is the dialog's one focusable element.
            if (e.key === 'Tab') e.preventDefault()
          }}
        >
          <div class="keyhelp-h">
            <h2 id="keyhelp-title">Keyboard shortcuts</h2>
            <button type="button" class="proj-act" ref={close} onClick={() => setOpen(false)}>
              Close
            </button>
          </div>
          <table class="keyhelp-t">
            <thead>
              <tr>
                <th scope="col">Key</th>
                <th scope="col">Where</th>
                <th scope="col">What it does</th>
              </tr>
            </thead>
            <tbody>
              <For each={KEYS}>
                {(k) => (
                  <tr>
                    <td>
                      <kbd>{k.keys}</kbd>
                    </td>
                    <td>{k.where}</td>
                    <td>{k.does}</td>
                  </tr>
                )}
              </For>
            </tbody>
          </table>
          <p class="keyhelp-n">Keys do nothing while you type in a field or a dialog is open.</p>
        </div>
      </div>
    </Show>
  )
}
