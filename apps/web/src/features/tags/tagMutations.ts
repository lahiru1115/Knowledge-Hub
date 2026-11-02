import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { invalidateLibrary } from '@/features/library/mutations'
import { notifyError, notifyUndo } from '@/lib/toasts'
import type { Tag } from '@/features/library/types'

export function useRenameTag() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, name }: { id: string; name: string }) =>
      unwrap(api.PATCH('/tags/{id}', { params: { path: { id } }, body: { name } })),
    onSuccess: () => invalidateLibrary(qc),
  })
}

export function useMergeTag() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, target }: { id: string; target: string }) =>
      unwrap(
        api.POST('/tags/{id}/merge', { params: { path: { id } }, body: { target_tag_id: target } }),
      ),
    onSuccess: () => invalidateLibrary(qc),
  })
}

/** Every resource (archived ones too) that has the tag, so deleting it can be undone. */
async function resourceIdsWithTag(name: string): Promise<string[]> {
  const ids: string[] = []
  for (const archived of [false, true]) {
    for (let page = 1; ; page++) {
      const result = await unwrap(
        api.GET('/resources', {
          params: { query: { tag: [name], archived, page, page_size: 100 } },
        }),
      )
      ids.push(...result.items.map((r) => r.id))
      if (page * 100 >= result.total) break
    }
  }
  return ids
}

/**
 * Deletes tags (the resources stay) with an Undo toast. Undo puts the tag back on the resources it was on, by
 * adding it again in bulk, because the API has no "restore tag" (Doc 03 §5.7).
 */
export function useDeleteTags() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: async (tags: Tag[]) => {
      const removed: { name: string; ids: string[] }[] = []
      for (const t of tags) {
        const ids = await resourceIdsWithTag(t.name)
        await unwrap(api.DELETE('/tags/{id}', { params: { path: { id: t.id } } }))
        removed.push({ name: t.name, ids })
      }
      return removed
    },
    onSuccess: async (removed) => {
      await invalidateLibrary(qc)
      const label =
        removed.length === 1
          ? `Deleted #${removed[0]?.name ?? ''}`
          : `Deleted ${removed.length.toString()} tags`
      notifyUndo(label, () => {
        void (async () => {
          for (const { name, ids } of removed) {
            if (ids.length === 0) {
              // Unused: bring the tag back by attaching and detaching is not possible, so nothing more to restore.
              continue
            }
            for (let i = 0; i < ids.length; i += 100) {
              await unwrap(
                api.POST('/resources/bulk', {
                  body: { action: 'add_tags', ids: ids.slice(i, i + 100), tags: [name] },
                }),
              )
            }
          }
          await invalidateLibrary(qc)
        })().catch(notifyError)
      })
    },
    onError: (e) => {
      notifyError(e)
    },
  })
}
