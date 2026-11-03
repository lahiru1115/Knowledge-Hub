import { queryOptions, useMutation, useQueryClient } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import type { components } from '@/api/schema'
import { aiKeys } from '@/features/ai/queries'

export type ChatSession = components['schemas']['ChatSession']
export type ChatMessage = components['schemas']['ChatMessage']
export type Citation = components['schemas']['Citation']

export const chatKeys = {
  sessions: ['chat', 'sessions'] as const,
  session: (id: string) => ['chat', 'session', id] as const,
}

export const chatSessionsQuery = queryOptions({
  queryKey: chatKeys.sessions,
  queryFn: () => unwrap(api.GET('/chat/sessions')),
  retry: false,
})

export const chatSessionQuery = (id: string) =>
  queryOptions({
    queryKey: chatKeys.session(id),
    queryFn: () => unwrap(api.GET('/chat/sessions/{id}', { params: { path: { id } } })),
    retry: false,
  })

export function useCreateChat() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (scopeCollectionId: string | null) =>
      unwrap(api.POST('/chat/sessions', { body: { scope_collection_id: scopeCollectionId } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: chatKeys.sessions }),
  })
}

export function useRenameChat() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: ({ id, title }: { id: string; title: string }) =>
      unwrap(api.PATCH('/chat/sessions/{id}', { params: { path: { id } }, body: { title } })),
    onSuccess: (_s, { id }) =>
      Promise.all([
        qc.invalidateQueries({ queryKey: chatKeys.sessions }),
        qc.invalidateQueries({ queryKey: chatKeys.session(id) }),
      ]),
  })
}

export function useDeleteChat() {
  const qc = useQueryClient()
  return useMutation({
    mutationFn: (id: string) =>
      unwrap(api.DELETE('/chat/sessions/{id}', { params: { path: { id } } })),
    onSuccess: () => qc.invalidateQueries({ queryKey: chatKeys.sessions }),
  })
}

/** Refreshes what an answer changed: the chat itself, the list (title, order) and today's usage. */
export function invalidateAfterAnswer(qc: ReturnType<typeof useQueryClient>, id: string) {
  return Promise.all([
    qc.invalidateQueries({ queryKey: chatKeys.session(id) }),
    qc.invalidateQueries({ queryKey: chatKeys.sessions }),
    qc.invalidateQueries({ queryKey: aiKeys.usage }),
  ])
}
