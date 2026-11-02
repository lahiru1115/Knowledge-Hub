import { z } from 'zod'
import { RESOURCE_TYPES, type ResourceType } from './types'

const list = z
  .union([z.string(), z.array(z.string())])
  .transform((v) => (Array.isArray(v) ? v : [v]))

const bool = z.union([z.boolean(), z.enum(['true', 'false']).transform((v) => v === 'true')])

/** The library's state, kept in the URL so a filtered view can be bookmarked and Back works (Doc 03 §5.2). */
export const librarySearchSchema = z.object({
  q: z.string().optional().catch(undefined),
  type: list.optional().catch(undefined),
  tag: list.optional().catch(undefined),
  collection: z.string().optional().catch(undefined),
  favourite: bool.optional().catch(undefined),
  archived: bool.optional().catch(undefined),
  sort: z.enum(['relevance', 'newest', 'oldest', 'title', 'updated']).optional().catch(undefined),
  page: z.coerce.number().int().min(1).optional().catch(undefined),
  size: z.coerce.number().int().min(1).max(100).optional().catch(undefined),
  /** The resource open in the drawer. */
  r: z.string().optional().catch(undefined),
})

export type LibrarySearch = z.infer<typeof librarySearchSchema>

export const DEFAULT_PAGE_SIZE = 20

const validTypes = new Set<string>(RESOURCE_TYPES.map((t) => t.value))

/** The request parameters for GET /resources, from the URL state. */
export function toApiQuery(s: LibrarySearch, scopeCollection?: string) {
  return {
    q: s.q || undefined,
    type: s.type?.filter((t): t is ResourceType => validTypes.has(t)),
    tag: s.tag,
    collection_id: scopeCollection ?? s.collection,
    favourite: s.favourite || undefined,
    archived: s.archived || undefined,
    sort: s.sort ?? (s.q ? 'relevance' : 'newest'),
    page: s.page ?? 1,
    page_size: s.size ?? DEFAULT_PAGE_SIZE,
  }
}

export type ApiQuery = ReturnType<typeof toApiQuery>

/** How many filters are active (not counting the query, sort or page). */
export function activeFilterCount(s: LibrarySearch): number {
  return (
    (s.type?.length ?? 0) +
    (s.tag?.length ?? 0) +
    (s.collection ? 1 : 0) +
    (s.favourite ? 1 : 0) +
    (s.archived ? 1 : 0)
  )
}
