import { useSuspenseQuery } from '@tanstack/react-query'
import { createFileRoute } from '@tanstack/react-router'
import { meQueryOptions } from '@/features/auth/session'

export const Route = createFileRoute('/_app/')({ component: Home })

function Home() {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  return (
    <section className="space-y-2">
      <h1 className="text-3xl font-semibold">Welcome, {me.display_name}</h1>
      <p className="text-text-muted">Your library will appear here.</p>
    </section>
  )
}
