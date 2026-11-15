import { createSignal } from 'solid-js'
import { THEME_KEY, storedForTheme, themeFromStored, toggledTheme, type ThemeChoice } from './account.ts'

/** Storage can throw outright (a private window, blocked site data); a theme is not worth a crash. */
function stored(): ThemeChoice {
  try {
    return themeFromStored(localStorage.getItem(THEME_KEY))
  } catch {
    return 'system'
  }
}

const [themeChoice, setChoice] = createSignal<ThemeChoice>('system')
export { themeChoice }

function apply(choice: ThemeChoice): void {
  const root = document.documentElement
  if (choice === 'system') delete root.dataset.theme
  else root.dataset.theme = choice
}

/** System leaves `data-theme` unset, so the CSS follows `prefers-color-scheme`. */
export function initTheme(): void {
  const choice = stored()
  setChoice(choice)
  apply(choice)
}

/** The header menu's System / Light / Dark: shown at once and remembered; System removes the stored value. */
export function setTheme(choice: ThemeChoice): void {
  setChoice(choice)
  apply(choice)
  try {
    const value = storedForTheme(choice)
    if (value === null) localStorage.removeItem(THEME_KEY)
    else localStorage.setItem(THEME_KEY, value)
  } catch {
    // remembered for this page only
  }
}

/** The palette's Toggle theme: flips whatever is showing now, explicit or inherited from the system. */
export function toggleTheme(): void {
  setTheme(toggledTheme(themeChoice(), matchMedia('(prefers-color-scheme: dark)').matches))
}
