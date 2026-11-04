import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { invalidateLibrary } from '@/features/library/mutations'
import { keys } from '@/features/library/queries'
import { aiKeys } from './queries'

/** POST /resources/{id}/summary: generates (or regenerates) the summary and returns the updated resource. */
export function useGenerateSummary() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) =>
      unwrap(api.POST('/resources/{id}/summary', { params: { path: { id } } })),
    onSuccess: (resource) => {
      qc.setQueryData(keys.resource(resource.id), resource)
      void invalidateLibrary(qc)
      void qc.invalidateQueries({ queryKey: aiKeys.usage })
    },
  })
}
