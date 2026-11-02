import { useMutation, useQueryClient, type QueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { notifyError, notifyUndo } from '@/lib/toasts'
import { keys } from './queries'
import type { BulkRequest, Resource, ResourceCreate, ResourcePage, ResourceUpdate } from './types'

/** Everything that shows counts or lists is refetched after a change (Doc 03 §12.3). */
export function invalidateLibrary(qc: QueryClient) {
  return Promise.all([
    qc.invalidateQueries({ queryKey: keys.resources }),
    qc.invalidateQueries({ queryKey: ['resource'] }),
    qc.invalidateQueries({ queryKey: keys.collections }),
    qc.invalidateQueries({ queryKey: keys.tags }),
  ])
}

type ListSnapshot = [readonly unknown[], ResourcePage | undefined][]

/** Applies `change` to a resource in the detail cache and in every cached list page (optimistic update). */
function patchCached(qc: QueryClient, id: string, change: (r: Resource) => Resource | null) {
  const detail = qc.getQueryData<Resource>(keys.resource(id))
  if (detail) {
    const next = change(detail)
    if (next) qc.setQueryData(keys.resource(id), next)
  }
  const snapshot: ListSnapshot = qc.getQueriesData<ResourcePage>({ queryKey: keys.resources })
  for (const [key, page] of snapshot) {
    if (!page) continue
    const items: Resource[] = []
    let removed = 0
    for (const item of page.items) {
      if (item.id !== id) {
        items.push(item)
        continue
      }
      const next = change(item)
      if (next) items.push(next)
      else removed++
    }
    qc.setQueryData(key, { ...page, items, total: page.total - removed })
  }
  return { detail, snapshot }
}

function rollback(
  qc: QueryClient,
  id: string,
  state: { detail?: Resource; snapshot: ListSnapshot },
) {
  if (state.detail) qc.setQueryData(keys.resource(id), state.detail)
  for (const [key, page] of state.snapshot) qc.setQueryData(key, page)
}

export function useCreateResource() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: ResourceCreate) => unwrap(api.POST('/resources', { body })),
    onSuccess: () => invalidateLibrary(qc),
  })
}

/** Optimistic PATCH for toggles and moves; rolls back and shows an error toast if it fails (Doc 03 §6.1). */
export function useUpdateResource() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: ResourceUpdate }) =>
      unwrap(api.PATCH('/resources/{id}', { params: { path: { id } }, body })),
    onMutate: async ({ id, body }) => {
      await qc.cancelQueries({ queryKey: keys.resources })
      await qc.cancelQueries({ queryKey: keys.resource(id) })
      return patchCached(qc, id, (r) => ({
        ...r,
        ...(body.is_favourite !== undefined ? { is_favourite: body.is_favourite } : {}),
        ...(body.is_archived !== undefined ? { is_archived: body.is_archived } : {}),
        ...('collection_id' in (body as object)
          ? { collection_id: (body as { collection_id?: string | null }).collection_id }
          : {}),
      }))
    },
    onError: (error, { id }, state) => {
      if (state) rollback(qc, id, state)
      notifyError(error)
    },
    onSettled: () => invalidateLibrary(qc),
  })
}

/** Deletes at once with an Undo toast; the API keeps it for 7 days (FR-RES-04). */
export function useDeleteResource() {
  const qc = useQueryClient()
  const restore = useMutation({
    mutationFn: (id: string) =>
      unwrap(api.POST('/resources/{id}/restore', { params: { path: { id } } })),
    onSettled: () => invalidateLibrary(qc),
    onError: (e) => {
      notifyError(e)
    },
  })
  return useMutation({
    mutationFn: (r: Pick<Resource, 'id' | 'title' | 'url'>) =>
      unwrap(api.DELETE('/resources/{id}', { params: { path: { id: r.id } } })),
    onMutate: async (r) => {
      await qc.cancelQueries({ queryKey: keys.resources })
      return patchCached(qc, r.id, () => null)
    },
    onError: (error, r, state) => {
      if (state) rollback(qc, r.id, state)
      notifyError(error)
    },
    onSuccess: (_data, r) => {
      notifyUndo(`Deleted '${r.title ?? r.url}'`, () => {
        restore.mutate(r.id)
      })
    },
    onSettled: () => invalidateLibrary(qc),
  })
}

/** Bulk move, tag, remove tags, delete and restore (FR-RES-12). Delete and tag changes offer Undo. */
export function useBulk() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: BulkRequest) => unwrap(api.POST('/resources/bulk', { body })),
    onSuccess: async (result, body) => {
      await invalidateLibrary(qc)
      const n = result.changed
      const ids = body.ids
      if (body.action === 'delete') {
        notifyUndo(`Deleted ${n.toString()} resource${n === 1 ? '' : 's'}`, () => {
          void unwrap(api.POST('/resources/bulk', { body: { action: 'restore', ids } }))
            .then(() => invalidateLibrary(qc))
            .catch(notifyError)
        })
      }
    },
    onError: (e) => {
      notifyError(e)
    },
  })
}
