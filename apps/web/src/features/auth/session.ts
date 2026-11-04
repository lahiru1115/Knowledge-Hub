import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { useRouter } from '@tanstack/react-router'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'

export type User = components['schemas']['User']

/** The signed-in user. `retry: false` because a 401 already went through the refresh flow. */
export const meQueryOptions = queryOptions({
  queryKey: ['me'],
  queryFn: () => unwrap(api.GET('/me')),
  retry: false,
  staleTime: 5 * 60_000,
})

export function useSignOut() {
  const queryClient = useQueryClient()
  const router = useRouter()
  return useMutation({
    mutationFn: () => unwrap(api.POST('/auth/logout')),
    onSettled: async () => {
      // Whatever the server said, this browser is done with the session.
      queryClient.clear()
      await router.navigate({ to: '/login', search: {} })
    },
  })
}
