import { queryOptions, useQuery } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'
import { meQueryOptions } from '@/features/auth/session'

export type AiStatus = components['schemas']['AiStatus']
export type AiUsage = components['schemas']['AiUsage']

export const aiKeys = {
  status: ['ai', 'status'] as const,
  usage: ['ai', 'usage'] as const,
  related: (id: string) => ['ai', 'related', id] as const,
}

/** Whether the AI service is up and which profile runs. The Core API caches it for 10 seconds, so polling is cheap. */
export const aiStatusQuery = queryOptions({
  queryKey: aiKeys.status,
  queryFn: () => unwrap(api.GET('/ai/status')),
  staleTime: 10_000,
  retry: false,
})

export const aiUsageQuery = queryOptions({
  queryKey: aiKeys.usage,
  queryFn: () => unwrap(api.GET('/me/ai-usage')),
  staleTime: 10_000,
  retry: false,
})

/** What the interface may offer: AI features, only while the service answers (Doc 03: "AI unavailable" states). */
export function useAi() {
  const status = useQuery(aiStatusQuery)
  const me = useQuery(meQueryOptions)
  return {
    /** True once known to be up. While loading it is false, so AI controls start disabled rather than flicker on. */
    available: status.data?.available === true,
    chatAvailable: status.data?.available === true && status.data.chat_available === true,
    profile: status.data?.profile,
    loading: status.isPending,
    autoSummary: me.data?.preferences.ai_auto_summary ?? true,
    tagSuggestions: me.data?.preferences.ai_tag_suggestions ?? true,
  }
}

export const UNAVAILABLE_TIP = 'AI features are unavailable right now.'
