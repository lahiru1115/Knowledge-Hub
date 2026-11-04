import { Link, Outlet } from '@tanstack/react-router'
import { useSuspenseQuery } from '@tanstack/react-query'
import { meQueryOptions } from '@/features/auth/session'

const sections = [
  { to: '/admin/users', label: 'Users' },
  { to: '/admin/ai-usage', label: 'AI usage' },
] as const

/**
 * Administration (SCR-ADMIN). Only administrators see it: the API refuses everyone else, and this page does not
 * ask it anything for them (FR-ADM-04).
 */
export function AdminLayout() {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  if (me.role !== 'admin') {
    return (
      <section aria-labelledby="no-access" className="mx-auto mt-12 max-w-md space-y-3 text-center">
        <h1 id="no-access" className="text-xl font-semibold">
          You don&apos;t have access to this
        </h1>
        <p className="text-muted-foreground">Administration is only for administrators.</p>
        <Link to="/" className="text-sm underline">
          Back to Home
        </Link>
      </section>
    )
  }
  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-end justify-between gap-3">
        <h1 className="text-2xl font-semibold">Admin</h1>
        <nav aria-label="Admin sections">
          <ul className="flex gap-1">
            {sections.map((s) => (
              <li key={s.to}>
                <Link
                  to={s.to}
                  className="block rounded-md px-3 py-2 text-sm hover:bg-accent hover:text-accent-foreground"
                  activeProps={{
                    className: 'bg-accent font-medium text-accent-foreground',
                    'aria-current': 'page',
                  }}
                >
                  {s.label}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
      </div>
      <Outlet />
    </div>
  )
}
