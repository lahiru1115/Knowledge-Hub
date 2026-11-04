import { keepPreviousData, queryOptions } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'
import { meQueryOptions } from '@/features/auth/session'
import { useQuery } from '@tanstack/react-query'

export type AdminUser = components['schemas']['AdminUser']

export const adminUsersQuery = (q: string, page: number, pageSize: number) =>
  queryOptions({
    queryKey: ['admin', 'users', { q, page, pageSize }] as const,
    queryFn: () =>
      unwrap(
        api.GET('/admin/users', {
          params: { query: { q: q || undefined, page, page_size: pageSize } },
        }),
      ),
    placeholderData: keepPreviousData,
    retry: false,
  })

export const adminUsageQuery = (userId?: string) =>
  queryOptions({
    queryKey: ['admin', 'ai-usage', userId ?? 'all'] as const,
    queryFn: () => unwrap(api.GET('/admin/ai-usage', { params: { query: { user_id: userId } } })),
    retry: false,
  })

/** Whether the signed-in user is an administrator. False while that is still loading, so nothing flashes. */
export function useIsAdmin(): boolean {
  return useQuery(meQueryOptions).data?.role === 'admin'
}
