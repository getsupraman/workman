import type { ManyPrompts, Overview, Prompt } from './api.ts'

/** `/api/v1/prompts?scopes=` refuses more ids than this. */
export const PROMPTS_MAX = 200

/**
 * The scopes the Overview reads prompts for, as one key: every project card it can show, archived ones included, in
 * the overview's order. `undefined` while there is none, so nothing is asked.
 */
export function promptScopes(overview: Overview | undefined): string | undefined {
  const ids = (overview?.projects ?? []).map((p) => p.item.id)
  return ids.length === 0 ? undefined : ids.join(',')
}

/** The reads a key takes: at most `PROMPTS_MAX` ids each, so a workspace past it is one read per chunk, not per card. */
export function promptChunks(key: string): string[][] {
  const ids = key.split(',')
  const chunks: string[][] = []
  for (let at = 0; at < ids.length; at += PROMPTS_MAX) chunks.push(ids.slice(at, at + PROMPTS_MAX))
  return chunks
}

/** Each scope's prompts by its id; a scope the reads left out (one the caller does not see) has none. */
export function promptsByScope(reads: ManyPrompts[]): Record<string, Prompt[]> {
  return Object.fromEntries(reads.flatMap((read) => read.scopes.map((s) => [s.scope.id, s.prompts])))
}
