import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { Plus } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { meQueryOptions } from '@/features/auth/session'
import { useQuickAdd } from '@/features/library/QuickAdd'
import { TypeIcon } from '@/features/library/ResourceRow'
import { plural, timeAgo } from '@/lib/format'
import { usePageTitle } from '@/lib/usePageTitle'
import { ActivityChart, TypeBars } from './Charts'
import { Checklist } from './Checklist'
import { dashboardQuery, type Dashboard } from './queries'

function Tile({
  n,
  label,
  to,
  search,
}: {
  n: number
  label: string
  to: string
  search?: Record<string, unknown>
}) {
  return (
    <Link
      to={to}
      search={search}
      className="block rounded-lg border p-4 hover:bg-accent focus-visible:ring-[3px] focus-visible:ring-ring/50 focus-visible:outline-none"
    >
      <span className="block text-3xl font-semibold tabular-nums">{n.toLocaleString('en')}</span>
      <span className="text-sm text-muted-foreground">{label}</span>
    </Link>
  )
}

/** Home (SCR-HOME): the checklist for new users, the library in numbers, what was added lately, and activity. */
export function HomePage() {
  usePageTitle('Home')
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const quickAdd = useQuickAdd()
  const q = useQuery(dashboardQuery())

  return (
    <div className="space-y-6">
      <h1 className="text-3xl font-semibold">Welcome, {me.display_name}</h1>

      {q.isPending ? (
        <div
          aria-busy="true"
          aria-label="Loading your dashboard"
          role="status"
          className="space-y-4"
        >
          <div className="grid grid-cols-2 gap-3 md:grid-cols-4">
            {Array.from({ length: 4 }, (_, i) => (
              <Skeleton key={i} className="h-20" />
            ))}
          </div>
          <Skeleton className="h-40" />
        </div>
      ) : q.isError ? (
        <div
          role="alert"
          className="space-y-3 rounded-md border border-destructive p-6 text-center"
        >
          <p className="font-medium">Couldn&apos;t load your dashboard.</p>
          <Button variant="outline" onClick={() => void q.refetch()}>
            Try again
          </Button>
        </div>
      ) : (
        <DashboardBody
          d={q.data}
          dismissed={me.preferences.onboarding_dismissed ?? false}
          onAdd={() => {
            quickAdd.open()
          }}
        />
      )}
    </div>
  )
}

function DashboardBody({
  d,
  dismissed,
  onAdd,
}: {
  d: Dashboard
  dismissed: boolean
  onAdd: () => void
}) {
  const empty = d.totals.resources === 0
  return (
    <>
      <Checklist onboarding={{ ...d.onboarding, dismissed: d.onboarding.dismissed || dismissed }} />

      <section
        aria-label="Your library in numbers"
        className="grid grid-cols-2 gap-3 md:grid-cols-4"
      >
        <Tile
          n={d.totals.resources}
          label={d.totals.resources === 1 ? 'resource' : 'resources'}
          to="/library"
        />
        <Tile
          n={d.totals.collections}
          label={d.totals.collections === 1 ? 'collection' : 'collections'}
          to="/collections"
        />
        <Tile n={d.totals.tags} label={d.totals.tags === 1 ? 'tag' : 'tags'} to="/tags" />
        <Tile
          n={d.totals.favourites}
          label={d.totals.favourites === 1 ? 'favourite' : 'favourites'}
          to="/library"
          search={{ favourite: true }}
        />
      </section>

      {empty ? (
        <div className="space-y-3 rounded-lg border p-8 text-center">
          <p className="text-lg font-medium">Your library is empty.</p>
          <p className="text-muted-foreground">
            Save links to articles, videos, courses and repos. We&apos;ll fetch the details.
          </p>
          <Button onClick={onAdd}>
            <Plus aria-hidden="true" /> Add your first resource
          </Button>
          <p className="text-xs text-muted-foreground">Or press N.</p>
        </div>
      ) : (
        <div className="grid gap-6 md:grid-cols-2">
          <section aria-labelledby="recent-title" className="space-y-2">
            <h2 id="recent-title" className="font-semibold">
              Recently added
            </h2>
            <ul>
              {d.recent.map((r) => (
                <li key={r.id}>
                  <Link
                    to="/library"
                    search={{ r: r.id }}
                    className="flex items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent"
                  >
                    <TypeIcon type={r.type} className="size-4 shrink-0 text-muted-foreground" />
                    <span className="min-w-0 flex-1 truncate">{r.title ?? r.url}</span>
                    <span className="shrink-0 text-xs text-muted-foreground">
                      {timeAgo(r.created_at)}
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          </section>
          <section aria-labelledby="types-title" className="space-y-2">
            <h2 id="types-title" className="font-semibold">
              By type
            </h2>
            <TypeBars byType={d.by_type} />
          </section>
        </div>
      )}

      <section aria-labelledby="activity-title" className="space-y-2">
        <h2 id="activity-title" className="font-semibold">
          Activity{' '}
          <span className="text-sm font-normal text-muted-foreground">
            last 12 weeks ·{' '}
            {plural(
              d.weekly_activity.reduce((n, w) => n + w.count, 0),
              'resource',
            )}{' '}
            added
          </span>
        </h2>
        <ActivityChart weeks={d.weekly_activity} />
      </section>
    </>
  )
}
