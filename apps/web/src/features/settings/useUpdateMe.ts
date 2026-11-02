import { useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'
import type { User } from '@/features/auth/session'

type UpdateMe = components['schemas']['UpdateMeRequest']

/** PATCH /me; the response is the whole updated user, which replaces the cached one. */
export function useUpdateMe() {
  const queryClient = useQueryClient()
  return useMutation({
    mutationFn: (body: UpdateMe) => unwrap(api.PATCH('/me', { body })),
    onSuccess: (user: User) => {
      queryClient.setQueryData(['me'], user)
    },
  })
}
