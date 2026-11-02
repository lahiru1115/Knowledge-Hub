import { Link, Outlet } from '@tanstack/react-router'
import { useSuspenseQuery } from '@tanstack/react-query'
import { Button } from '@/components/ui/button'
import { meQueryOptions, useSignOut } from './session'

/** The signed-in layout. Sidebar, quick-add and the rest of the navigation arrive with the library. */
export function AppShell() {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const signOut = useSignOut()
  return (
    <div className="min-h-dvh">
      <header className="flex h-14 items-center justify-between border-b border-border px-4">
        <Link to="/" className="text-lg font-semibold">
          Knowledge Hub
        </Link>
        <div className="flex items-center gap-3">
          <span className="text-sm text-text-muted">{me.display_name}</span>
          <Button
            variant="secondary"
            loading={signOut.isPending}
            onClick={() => {
              signOut.mutate()
            }}
          >
            Sign out
          </Button>
        </div>
      </header>
      <main className="mx-auto max-w-3xl px-4 py-8">
        <Outlet />
      </main>
    </div>
  )
}
