import { AlertTriangle } from 'lucide-react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
import { Button } from '@/components/ui/button'

/** Shown by route and root error boundaries. The trace ID helps with bug reports (Doc 03 §10.3). */
export function ErrorPanel({ error, onRetry }: { error: unknown; onRetry?: () => void }) {
  const problem = error instanceof ApiProblem ? error : undefined
  return (
    <div role="alert" className="mx-auto mt-16 max-w-md space-y-4 px-4 text-center">
      <AlertTriangle className="mx-auto size-8 text-destructive" aria-hidden="true" />
      <h1 className="text-xl font-semibold">Something went wrong</h1>
      <p className="text-muted-foreground">
        {problem
          ? messageFor(problem.code, problem)
          : 'Something went wrong on our side. Try again in a moment.'}
      </p>
      {problem?.traceId && (
        <details className="text-sm text-muted-foreground">
          <summary className="cursor-pointer">Details</summary>
          <p className="mt-1 font-mono">Trace ID: {problem.traceId}</p>
        </details>
      )}
      <Button
        variant="outline"
        onClick={
          onRetry ??
          (() => {
            globalThis.location.reload()
          })
        }
      >
        Try again
      </Button>
    </div>
  )
}
