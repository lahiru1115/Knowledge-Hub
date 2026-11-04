import { Link } from '@tanstack/react-router'
import { Check, Circle } from 'lucide-react'
import type { ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { notifyError } from '@/lib/toasts'
import { useQuickAdd } from '@/features/library/QuickAdd'
import { useUpdateMe } from '@/features/settings/useUpdateMe'
import type { Dashboard } from './queries'

/** "Get started" (FR-DASH-05): steps tick themselves; the user can dismiss it at any time. */
export function Checklist({ onboarding }: { onboarding: Dashboard['onboarding'] }) {
  const quickAdd = useQuickAdd()
  const update = useUpdateMe()
  const { steps } = onboarding
  const items: { done: boolean; label: string; action: ReactNode }[] = [
    {
      done: steps.first_resource,
      label: 'Save your first resource',
      action: (
        <Button
          variant="link"
          className="h-auto p-0"
          onClick={() => {
            quickAdd.open()
          }}
        >
          Add one
        </Button>
      ),
    },
    {
      done: steps.first_collection,
      label: 'Create a collection',
      action: (
        <Button asChild variant="link" className="h-auto p-0">
          <Link to="/collections">Go to collections</Link>
        </Button>
      ),
    },
    {
      done: steps.tried_search,
      label: 'Try a search',
      action: (
        <Button asChild variant="link" className="h-auto p-0">
          <Link to="/library">Open the library</Link>
        </Button>
      ),
    },
    {
      done: steps.asked_question,
      label: 'Ask a question',
      action: (
        <Button asChild variant="link" className="h-auto p-0">
          <Link to="/ask">Ask your library</Link>
        </Button>
      ),
    },
  ]
  const done = items.filter((i) => i.done).length
  if (onboarding.dismissed || done === items.length) return null

  return (
    <section aria-labelledby="get-started" className="space-y-3 rounded-lg border p-4">
      <div className="flex items-center justify-between gap-2">
        <h2 id="get-started" className="font-semibold">
          Get started
          <span className="ml-2 text-sm font-normal text-muted-foreground">
            {done.toString()} of {items.length.toString()}
          </span>
        </h2>
        <Button
          variant="ghost"
          size="sm"
          disabled={update.isPending}
          onClick={() => {
            update.mutate(
              { preferences: { onboarding_dismissed: true } },
              {
                onError: (e) => {
                  notifyError(e)
                },
              },
            )
          }}
        >
          Dismiss
        </Button>
      </div>
      <ul className="grid gap-2 sm:grid-cols-2">
        {items.map((i) => (
          <li key={i.label} className="flex items-center gap-2 text-sm">
            {i.done ? (
              <Check className="size-4 text-success" aria-hidden="true" />
            ) : (
              <Circle className="size-4 text-muted-foreground" aria-hidden="true" />
            )}
            <span className={i.done ? 'text-muted-foreground line-through' : undefined}>
              {i.label}
              <span className="sr-only">{i.done ? ' (done)' : ' (not done yet)'}</span>
            </span>
            {!i.done && i.action}
          </li>
        ))}
      </ul>
    </section>
  )
}
