import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { createFileRoute, Link } from '@tanstack/react-router'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { meQueryOptions } from '@/features/auth/session'
import { useQuickAdd } from '@/features/library/QuickAdd'
import { collectionsQuery } from '@/features/library/queries'
import { usePageTitle } from '@/lib/usePageTitle'

export const Route = createFileRoute('/_app/')({ component: Home })

function Home() {
  usePageTitle('Home')
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const { data: collections } = useQuery(collectionsQuery)
  const quickAdd = useQuickAdd()
  const total =
    (collections?.items.reduce((n, c) => n + c.resource_count, 0) ?? 0) +
    (collections?.inbox_count ?? 0)
  return (
    <section className="space-y-4">
      <h1 className="text-3xl font-semibold">Welcome, {me.display_name}</h1>
      <p className="text-muted-foreground">
        {collections
          ? total === 0
            ? 'Your library is empty.'
            : `You have ${total.toLocaleString('en')} saved resource${total === 1 ? '' : 's'}.`
          : ''}
      </p>
      <div className="flex flex-wrap gap-2">
        <Button
          onClick={() => {
            quickAdd.open()
          }}
        >
          <Plus aria-hidden="true" /> Add a resource
        </Button>
        <Button asChild variant="outline">
          <Link to="/library">Open your library</Link>
        </Button>
      </div>
    </section>
  )
}
