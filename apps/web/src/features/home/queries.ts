import { queryOptions } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'

export type Dashboard = components['schemas']['Dashboard']

/** The browser's own time zone, so "this week" means the user's week (FR-DASH-04). */
export function userTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone
  } catch {
    return 'UTC'
  }
}

export const dashboardQuery = (timeZone = userTimeZone()) =>
  queryOptions({
    queryKey: ['dashboard', timeZone] as const,
    queryFn: () => unwrap(api.GET('/dashboard', { params: { query: { time_zone: timeZone } } })),
    retry: false,
  })
