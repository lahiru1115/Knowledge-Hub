import { useQuery } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { ApiProblem, api, unwrap } from '@/api/client'
import { messageFor } from '@/api/problems'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { AiMark } from './AiMark'

interface Props {
  /** Exactly one of these: a saved resource, or a link that is not saved yet. */
  resourceId?: string
  url?: string
  /** Tags the resource already has; suggestions for these are left out. */
  current: string[]
  /** Switched off in Settings → AI (FR-AI-12): nothing is requested and nothing is shown. */
  enabled: boolean
  onAccept: (tags: string[]) => void
}

/**
 * "✦ Suggested tags" (Doc 03 §5.1, §9). Suggestions are only ever offered: clicking one adds it as a normal tag,
 * and nothing is applied on its own. Existing tags come first (the API orders them).
 */
export function SuggestedTags({ resourceId, url, current, enabled, onAccept }: Props) {
  const key = resourceId ?? url ?? ''
  const q = useQuery({
    queryKey: ['ai', 'tag-suggestions', key],
    queryFn: () =>
      unwrap(
        api.POST('/ai/tag-suggestions', {
          body: resourceId ? { resource_id: resourceId } : { url },
        }),
      ),
    enabled: enabled && key !== '',
    staleTime: 5 * 60_000,
    retry: false,
  })
  if (!enabled || key === '') return null

  if (q.isPending)
    return (
      <div
        aria-busy="true"
        aria-label="Loading tag suggestions"
        className="flex items-center gap-2"
      >
        <Skeleton className="h-6 w-16 rounded-full" />
        <Skeleton className="h-6 w-20 rounded-full" />
      </div>
    )
  if (q.isError) {
    const message =
      q.error instanceof ApiProblem && q.error.code === 'ai_quota_exceeded'
        ? messageFor(q.error.code, q.error)
        : 'Tag suggestions are unavailable right now.'
    return <p className="text-xs text-muted-foreground">{message}</p>
  }
  if (!q.data.ai_available)
    return (
      <p className="text-xs text-muted-foreground">Tag suggestions are unavailable right now.</p>
    )

  const taken = new Set(current.map((t) => t.toLowerCase()))
  const items = q.data.items.filter((i) => !taken.has(i.name.toLowerCase()))
  if (items.length === 0) return null

  return (
    <div role="group" aria-label="Suggested tags" className="flex flex-wrap items-center gap-2">
      <span className="text-xs text-ai">
        <AiMark /> Suggested tags
      </span>
      {items.map((i) => (
        <Button
          key={i.name}
          type="button"
          variant="outline"
          size="sm"
          className="h-7 rounded-full border-ai/40 px-2.5 text-xs text-ai hover:text-ai"
          title={i.existing ? 'One of your tags' : 'A new tag'}
          aria-label={`Add tag ${i.name}`}
          onClick={() => {
            onAccept([i.name])
          }}
        >
          <Plus aria-hidden="true" className="size-3" /> {i.name}
        </Button>
      ))}
      {items.length >= 2 && (
        <Button
          type="button"
          variant="ghost"
          size="sm"
          className="h-7 px-2 text-xs"
          onClick={() => {
            onAccept(items.map((i) => i.name))
          }}
        >
          Accept all
        </Button>
      )}
    </div>
  )
}
