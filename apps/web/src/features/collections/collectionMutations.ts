import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { invalidateLibrary } from '@/features/library/mutations'
import { keys } from '@/features/library/queries'
import type { components } from '@/api/schema'
import { notifyError } from '@/lib/toasts'

type CollectionCreate = components['schemas']['CollectionCreate']
type CollectionUpdate = components['schemas']['CollectionUpdate']

export function useCreateCollection() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (body: CollectionCreate) => unwrap(api.POST('/collections', { body })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.collections }),
  })
}

export function useUpdateCollection() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, body }: { id: string; body: CollectionUpdate }) =>
      unwrap(api.PATCH('/collections/{id}', { params: { path: { id } }, body })),
    onSuccess: () => qc.invalidateQueries({ queryKey: keys.collections }),
  })
}

/** Pin and reorder show the new order at once and roll back if the server refuses (Doc 03 §6.1). */
export function useReorderCollections() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (ids: string[]) => unwrap(api.PUT('/collections/order', { body: { ids } })),
    onError: (e) => {
      notifyError(e)
    },
    onSettled: () => qc.invalidateQueries({ queryKey: keys.collections }),
  })
}

export function useDeleteCollection() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({
      id,
      resources,
      target,
    }: {
      id: string
      resources: 'delete' | 'move'
      target?: string | null
    }) =>
      unwrap(
        api.DELETE('/collections/{id}', {
          params: {
            path: { id },
            query: { resources, ...(target ? { target_collection_id: target } : {}) },
          },
        }),
      ),
    onSuccess: () => invalidateLibrary(qc),
  })
}
