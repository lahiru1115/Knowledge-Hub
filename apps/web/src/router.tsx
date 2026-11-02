import type { QueryClient } from '@tanstack/react-query'
import { createRouter } from '@tanstack/react-router'
import { parseSearch, stringifySearch } from '@/lib/searchParams'
import { routeTree } from './routeTree.gen'

export function createAppRouter(
  queryClient: QueryClient,
  history?: Parameters<typeof createRouter>[0]['history'],
) {
  return createRouter({
    routeTree,
    history,
    context: { queryClient },
    parseSearch,
    stringifySearch,
    defaultPreload: 'intent',
    defaultErrorComponent: undefined,
  })
}

declare module '@tanstack/react-router' {
  interface Register {
    router: ReturnType<typeof createAppRouter>
  }
}
