import type { Tag } from '@/features/library/types'

/**
 * Plurals and "-sql" endings are the same idea for tagging purposes: react/reacts, postgres/postgresql. The "sql"
 * ending goes first, then one plural "s", so postgres, postgresql and postgres-sql all reduce to "postgre".
 */
function stem(name: string): string {
  return name
    .toLowerCase()
    .replace(/[\s_.-]+/g, '')
    .replace(/sql$/, '')
    .replace(/s$/, '')
}

/** A likelier-duplicate tag for `tag` among `all`, if any (Doc 03 §5.7). Computed in the browser; no AI. */
export function similarTo(tag: Tag, all: Tag[]): Tag | undefined {
  const base = stem(tag.name)
  if (base.length < 3) return undefined
  return all
    .filter((t) => t.id !== tag.id && stem(t.name) === base)
    .sort((a, b) => b.resource_count - a.resource_count)[0]
}
