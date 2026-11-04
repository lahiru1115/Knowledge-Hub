import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { cn } from '@/lib/utils'
import { METADATA_ERRORS } from './metadata'
import { useRefreshMetadata } from './mutations'
import type { Resource } from './types'

/**
 * Shows whether a link's details are still being fetched or could not be fetched, with Retry when trying again could
 * help (FR-META-03). Nothing is shown once the details are ready.
 */
export function FetchStatus({
  resource: r,
  className,
}: {
  resource: Resource
  className?: string
}) {
  const refresh = useRefreshMetadata()
  const title = r.title ?? r.url
  if (r.metadata_status === 'pending')
    return (
      <div className={cn('flex items-center gap-2 text-sm text-muted-foreground', className)}>
        <Skeleton className="h-3 w-20" aria-hidden="true" />
        Fetching details…
      </div>
    )
  if (r.metadata_status === 'failed') {
    const reason = r.metadata_error ? METADATA_ERRORS[r.metadata_error] : undefined
    return (
      <div className={cn('text-sm text-muted-foreground', className)}>
        Couldn't fetch details
        {reason?.retry && <span className="sr-only">. {reason.message}</span>}
        {(reason?.retry ?? true) && (
          <>
            {' · '}
            <Button
              type="button"
              variant="link"
              className="h-auto p-0 text-sm"
              aria-label={`Retry fetching details for ${title}`}
              disabled={refresh.isPending}
              onClick={() => {
                refresh.mutate(r.id)
              }}
            >
              Retry
            </Button>
          </>
        )}
        {reason && !reason.retry && <span> · {reason.message}</span>}
      </div>
    )
  }
  return null
}
