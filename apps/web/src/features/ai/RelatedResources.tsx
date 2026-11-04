import { useQuery } from '@tanstack/react-query'
import { api, unwrap } from '@/api/client'
import { TypeIcon } from '@/features/library/ResourceRow'
import { AiMark } from './AiMark'
import { aiKeys } from './queries'

/**
 * "RELATED ✦" in the drawer (FR-AI-07): up to 5 other resources nearest in meaning. When there are none, or the AI
 * service is down, the section is left out altogether rather than explained (Doc 03 §10.2).
 */
export function RelatedResources({ id, onOpen }: { id: string; onOpen: (id: string) => void }) {
  const q = useQuery({
    queryKey: aiKeys.related(id),
    queryFn: () => unwrap(api.GET('/resources/{id}/related', { params: { path: { id } } })),
    staleTime: 60_000,
    retry: false,
  })
  if (!q.data || q.data.items.length === 0) return null
  return (
    <section aria-labelledby="related-title" className="space-y-2">
      <h3
        id="related-title"
        className="text-xs font-semibold tracking-wide text-muted-foreground uppercase"
      >
        Related <AiMark />
      </h3>
      <ul className="space-y-1">
        {q.data.items.map((r) => (
          <li key={r.id}>
            <button
              type="button"
              className="flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-left text-sm hover:bg-accent"
              onClick={() => {
                onOpen(r.id)
              }}
            >
              <TypeIcon type={r.type} className="size-4 shrink-0 text-muted-foreground" />
              <span className="truncate">{r.title ?? r.url}</span>
            </button>
          </li>
        ))}
      </ul>
    </section>
  )
}
