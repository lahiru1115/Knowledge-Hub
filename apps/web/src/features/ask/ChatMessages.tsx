import { Copy, RotateCcw } from 'lucide-react'
import { Fragment, type ReactNode } from 'react'
import { toast } from 'sonner'
import { messageFor } from '@/api/problems'
import { AiMark } from '@/features/ai/AiMark'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import type { Citation } from './queries'
import type { Turn } from './useAsk'

/** Turns `[1]` in the text into buttons that open the cited resource, for the markers that really are citations. */
export function AnswerText({
  text,
  citations,
  onOpen,
}: {
  text: string
  citations: Citation[]
  onOpen: (resourceId: string) => void
}) {
  const byN = new Map(citations.map((c) => [c.n, c]))
  const parts: ReactNode[] = []
  let last = 0
  for (const m of text.matchAll(/\[(\d+)\]/g)) {
    const cite = byN.get(Number(m[1]))
    if (!cite) continue
    parts.push(text.slice(last, m.index))
    parts.push(
      <button
        key={m.index}
        type="button"
        aria-label={`Citation ${cite.n.toString()}: ${cite.title}`}
        title={cite.quote ?? cite.title}
        className="mx-0.5 rounded bg-ai-subtle px-1 text-xs font-medium text-ai hover:underline dark:bg-muted"
        onClick={() => {
          onOpen(cite.resource_id)
        }}
      >
        {cite.n}
      </button>,
    )
    last = m.index + m[0].length
  }
  parts.push(text.slice(last))
  return (
    <p className="text-sm leading-relaxed break-words whitespace-pre-wrap">
      {parts.map((p, i) => (
        <Fragment key={i}>{p}</Fragment>
      ))}
    </p>
  )
}

/** "Sources: [1 Title] [2 Title]", shown when an answer has finished (Doc 03 §5.5). */
export function Sources({
  citations,
  onOpen,
}: {
  citations: Citation[]
  onOpen: (resourceId: string) => void
}) {
  if (citations.length === 0) return null
  return (
    <div role="group" aria-label="Sources" className="flex flex-wrap items-center gap-2 text-xs">
      <span className="text-muted-foreground">Sources:</span>
      {citations.map((c) => (
        <Button
          key={c.n}
          type="button"
          variant="outline"
          size="sm"
          className="h-7 max-w-64 px-2 text-xs"
          aria-label={`Source ${c.n.toString()}: ${c.title}`}
          title={c.quote ?? c.title}
          onClick={() => {
            onOpen(c.resource_id)
          }}
        >
          <span aria-hidden="true">{c.n}</span>
          <span aria-hidden="true" className="truncate">
            {c.title}
          </span>
        </Button>
      ))}
    </div>
  )
}

function Who({ speaker }: { speaker: 'user' | 'assistant' }) {
  return (
    <p className="text-xs font-semibold text-muted-foreground">
      {speaker === 'user' ? (
        'You'
      ) : (
        <>
          <AiMark /> Knowledge Hub
        </>
      )}
    </p>
  )
}

export function UserMessage({ text }: { text: string }) {
  return (
    <div className="space-y-1">
      <Who speaker="user" />
      <p className="text-sm break-words whitespace-pre-wrap">{text}</p>
    </div>
  )
}

interface AssistantProps {
  text: string
  citations: Citation[]
  onOpen: (resourceId: string) => void
  /** Copy, Retry and the "search instead" link belong to a finished answer. */
  finished?: boolean
  onRetry?: () => void
  onSearchInstead?: () => void
}

export function AssistantMessage({
  text,
  citations,
  onOpen,
  finished = true,
  onRetry,
  onSearchInstead,
}: AssistantProps) {
  return (
    <div className="space-y-2">
      <Who speaker="assistant" />
      <AnswerText text={text} citations={citations} onOpen={onOpen} />
      {finished && <Sources citations={citations} onOpen={onOpen} />}
      {finished && (
        <div className="flex flex-wrap items-center gap-1">
          <Button
            variant="ghost"
            size="sm"
            onClick={() => {
              void navigator.clipboard.writeText(text).then(() => toast('Copied'))
            }}
          >
            <Copy aria-hidden="true" /> Copy
          </Button>
          {onRetry && (
            <Button variant="ghost" size="sm" onClick={onRetry}>
              <RotateCcw aria-hidden="true" /> Retry
            </Button>
          )}
          {citations.length === 0 && onSearchInstead && (
            <Button variant="outline" size="sm" onClick={onSearchInstead}>
              Search the library instead
            </Button>
          )}
        </div>
      )}
    </div>
  )
}

/** The question being answered right now: waiting, writing, stopped or failed (Doc 03 §5.5 states). */
export function LiveTurn({
  turn,
  onOpen,
  onRetry,
}: {
  turn: Turn
  onOpen: (resourceId: string) => void
  onRetry: () => void
}) {
  const failedBeforeAnswer = turn.status === 'failed' && turn.answer === ''
  return (
    <div className="space-y-4" aria-busy={turn.status === 'waiting' || turn.status === 'writing'}>
      <UserMessage text={turn.question} />
      {turn.status === 'waiting' ? (
        <div className="space-y-1">
          <Who speaker="assistant" />
          <p className="flex items-center gap-2 text-sm text-muted-foreground">
            <Spinner /> Searching your library…
          </p>
        </div>
      ) : failedBeforeAnswer ? null : (
        <AssistantMessage
          text={turn.answer}
          citations={turn.citations}
          onOpen={onOpen}
          finished={false}
        />
      )}
      {turn.status === 'writing' && <p className="text-xs text-muted-foreground">Writing…</p>}
      {turn.status === 'stopped' && (
        <p className="text-xs text-muted-foreground">
          Stopped. What was written so far is kept above.
        </p>
      )}
      {turn.status === 'failed' && (
        <div role="alert" className="space-y-2 rounded-md border border-destructive p-3 text-sm">
          <p>
            {failedBeforeAnswer
              ? messageFor((turn.error ?? 'internal_error') as Parameters<typeof messageFor>[0], {
                  resetAt: turn.resetAt,
                })
              : 'Answer interrupted.'}
          </p>
          {turn.error !== 'ai_quota_exceeded' && (
            <Button variant="outline" size="sm" onClick={onRetry}>
              <RotateCcw aria-hidden="true" /> Retry
            </Button>
          )}
        </div>
      )}
    </div>
  )
}
