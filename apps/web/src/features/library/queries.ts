import { keepPreviousData, queryOptions } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { ApiQuery } from './librarySearch'
import { fetchPollInterval } from './metadata'

/** Query keys follow the API (Doc 03 §12.3). */
export const keys = {
  resources: ['resources'] as const,
  resourceList: (q: ApiQuery) => ['resources', q] as const,
  resource: (id: string) => ['resource', id] as const,
  collections: ['collections'] as const,
  tags: ['tags'] as const,
}

export const resourcesQuery = (q: ApiQuery) =>
  queryOptions({
    queryKey: keys.resourceList(q),
    queryFn: () => unwrap(api.GET('/resources', { params: { query: q } })),
    placeholderData: keepPreviousData, // the old page stays visible while the next one loads
    refetchInterval: (query) => fetchPollInterval(query.state.data?.items),
  })

export const resourceQuery = (id: string) =>
  queryOptions({
    queryKey: keys.resource(id),
    queryFn: () => unwrap(api.GET('/resources/{id}', { params: { path: { id } } })),
    retry: false,
    refetchInterval: (query) =>
      fetchPollInterval(query.state.data ? [query.state.data] : undefined),
  })

export const collectionsQuery = queryOptions({
  queryKey: keys.collections,
  queryFn: () => unwrap(api.GET('/collections')),
})

export const tagsQuery = queryOptions({
  queryKey: keys.tags,
  queryFn: () => unwrap(api.GET('/tags')),
})
