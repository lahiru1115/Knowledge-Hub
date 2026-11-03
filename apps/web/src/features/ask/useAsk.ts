import { useQueryClient } from '@tanstack/react-query'
import { useCallback, useRef, useState } from 'react'
import { ApiProblem, api, unwrap } from '@/api/client'
import { readSse } from '@/lib/sse'
import { invalidateAfterAnswer, type Citation } from './queries'

/** One question and its answer as it arrives. Kept on screen until the saved chat has been reloaded. */
export interface Turn {
  question: string
  /** How many messages the saved chat had when this started, to tell when the saved copy has arrived. */
  baseCount: number
  answer: string
  citations: Citation[]
  /** waiting: no token yet ("Searching your library…"); writing: tokens arriving; done; stopped by the user; failed. */
  status: 'waiting' | 'writing' | 'done' | 'stopped' | 'failed'
  /** The problem code for a failure: from the stream's `error` event, or the response that refused to start one. */
  error?: string
  /** For quota failures. */
  resetAt?: string
}

function asCitations(data: unknown): Citation[] {
  const items = (data as { items?: unknown }).items
  return Array.isArray(items) ? (items as Citation[]) : []
}

/**
 * Asks a question and streams the answer (Doc 02 §8.5). Problems before the stream starts (limit reached, AI down)
 * end the turn as `failed` with the code. Stop aborts the request and keeps what has arrived.
 */
export function useAsk() {
  const qc = useQueryClient()
  const [turn, setTurn] = useState<Turn | null>(null)
  const abort = useRef<AbortController | null>(null)

  const ask = useCallback(
    async (sessionId: string, question: string, baseCount: number): Promise<boolean> => {
      const controller = new AbortController()
      abort.current = controller
      setTurn({ question, baseCount, answer: '', citations: [], status: 'waiting' })
      // Set from inside the stream callback, so kept on an object rather than in variables the compiler narrows.
      const end = { finished: false, succeeded: false } // finished: ended in `done` or `error`
      try {
        const body = await unwrap(
          api.POST('/chat/sessions/{id}/messages', {
            params: { path: { id: sessionId } },
            body: { content: question },
            parseAs: 'stream',
            signal: controller.signal,
          }),
        )
        if (!body) throw new ApiProblem({ status: 0, code: 'internal_error' })
        await readSse(body, ({ event, data }) => {
          if (event === 'token') {
            const text = (data as { text?: string }).text ?? ''
            setTurn((t) => t && { ...t, answer: t.answer + text, status: 'writing' })
          } else if (event === 'citations') {
            setTurn((t) => t && { ...t, citations: asCitations(data) })
          } else if (event === 'done') {
            end.finished = true
            end.succeeded = true
            setTurn((t) => t && { ...t, status: 'done' })
          } else if (event === 'error') {
            end.finished = true
            setTurn((t) => t && { ...t, status: 'failed', error: (data as { code?: string }).code })
          }
        })
        // The connection closed without `done` or `error`: treat as interrupted.
        if (!end.finished) setTurn((t) => t && { ...t, status: 'failed', error: 'ai_unavailable' })
      } catch (e) {
        if (controller.signal.aborted) {
          setTurn((t) => t && { ...t, status: 'stopped' })
          return false
        }
        setTurn(
          (t) =>
            t && {
              ...t,
              status: 'failed',
              error: e instanceof ApiProblem ? e.code : 'network_error',
              resetAt: e instanceof ApiProblem ? e.resetAt : undefined,
            },
        )
        return false
      } finally {
        abort.current = null
      }
      if (end.succeeded) {
        // Reload the saved chat before the in-flight turn goes away, so the answer never blinks out.
        await invalidateAfterAnswer(qc, sessionId)
        return true
      }
      return false
    },
    [qc],
  )

  return {
    turn,
    ask,
    stop: () => {
      abort.current?.abort()
    },
    clear: () => {
      setTurn(null)
    },
    busy: turn?.status === 'waiting' || turn?.status === 'writing',
  }
}
