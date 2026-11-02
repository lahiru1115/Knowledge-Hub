import type { QueryClient } from '@tanstack/react-query'
import { createRootRouteWithContext, Link, Outlet } from '@tanstack/react-router'
import { ErrorPanel } from '@/components/ErrorPanel'

export const Route = createRootRouteWithContext<{ queryClient: QueryClient }>()({
  component: Outlet,
  errorComponent: ({ error, reset }) => <ErrorPanel error={error} onRetry={reset} />,
  notFoundComponent: () => (
    <div className="mx-auto mt-16 max-w-md space-y-3 px-4 text-center">
      <h1 className="text-xl font-semibold">Page not found</h1>
      <p className="text-muted-foreground">This page doesn&apos;t exist.</p>
      <Link to="/" className="font-medium text-primary underline underline-offset-4">
        Go to home
      </Link>
    </div>
  ),
})
