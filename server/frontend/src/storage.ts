type Store = Pick<Storage, 'getItem' | 'setItem' | 'removeItem'>

/** The prefix the dashboard's keys had before server 2.0. */
const OLD_PREFIX = 'workstate.'

const NEW_PREFIX = 'workman.'

/**
 * Moves a choice remembered under the key's 1.x name to its `workman.*` one, once: the old value is
 * written under the new key unless that already holds one, and the old key goes. Storage can throw
 * outright (a private window, blocked site data); then nothing moves.
 */
export function adoptOldKey(key: string, store: () => Store = () => localStorage): void {
  if (!key.startsWith(NEW_PREFIX)) return
  const old = OLD_PREFIX + key.slice(NEW_PREFIX.length)
  try {
    const s = store()
    const value = s.getItem(old)
    if (value === null) return
    if (s.getItem(key) === null) s.setItem(key, value)
    s.removeItem(old)
  } catch {
    // stays where it was; the default applies
  }
}
