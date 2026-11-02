import { Link, Outlet } from '@tanstack/react-router'
import { useSuspenseQuery } from '@tanstack/react-query'
import { useEffect } from 'react'
import { LoadingButton } from '@/components/loading-button'
import { applyTheme } from '@/lib/theme'
import { meQueryOptions, useSignOut } from './session'

/** The signed-in layout. Sidebar, quick-add and the rest of the navigation arrive with the library. */
export function AppShell() {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const signOut = useSignOut()
  const theme = me.preferences.theme ?? 'system'
  // The server's preference wins, so the theme follows the user across devices.
  useEffect(() => {
    applyTheme(theme)
  }, [theme])
  return (
    <div className="min-h-dvh">
      <header className="flex h-14 items-center justify-between border-b border-border px-4">
        <Link to="/" className="text-lg font-semibold">
          Knowledge Hub
        </Link>
        <div className="flex items-center gap-3">
          <Link to="/settings/profile" className="text-sm font-medium underline">
            Settings
          </Link>
          <span className="text-sm text-muted-foreground">{me.display_name}</span>
          <LoadingButton
            variant="outline"
            loading={signOut.isPending}
            onClick={() => {
              signOut.mutate()
            }}
          >
            Sign out
          </LoadingButton>
        </div>
      </header>
      <main className="mx-auto max-w-3xl px-4 py-8">
        <Outlet />
      </main>
    </div>
  )
}
