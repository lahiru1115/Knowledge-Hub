import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider, createMemoryHistory } from '@tanstack/react-router'
import { render } from '@testing-library/react'
import { HttpResponse, http } from 'msw'
import { setSessionExpiredHandler } from '@/api/client'
import { createAppRouter } from '@/router'
import { server } from './server'

export const API = `${globalThis.location.origin}/api/v1`

export const user = {
  id: '0193a000-0000-7000-8000-000000000001',
  email: 'ada@example.com',
  display_name: 'Ada Lovelace',
  role: 'member',
  preferences: {
    theme: 'system',
    ai_auto_summary: true,
    ai_tag_suggestions: true,
    onboarding_dismissed: false,
  },
  created_at: '2026-11-02T08:00:00Z',
} as const

/** A browser with a valid session. */
export function signedInSession() {
  setCsrfCookie()
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(user)),
    http.post(`${API}/auth/logout`, () => new HttpResponse(null, { status: 204 })),
  )
}

export function problem(status: number, code: string, extra: Record<string, unknown> = {}) {
  return HttpResponse.json(
    { type: `/problems/${code}`, title: code, status, code, trace_id: 'abc123', ...extra },
    { status, headers: { 'Content-Type': 'application/problem+json' } },
  )
}

/** Simulates the Set-Cookie the real API sends: the jar is `document.cookie` in jsdom. */
export function setCsrfCookie(value = 'csrf-1') {
  document.cookie = `kh_csrf=${value}; path=/`
}

export interface Seed {
  collections?: unknown[]
  tags?: unknown[]
  resources?: unknown[]
}

/** The data the app shell loads in the background (collections, tags), so tests need not repeat it. */
export function libraryDefaults(seed: Seed = {}) {
  server.use(
    http.get(`${API}/collections`, () =>
      HttpResponse.json({ items: seed.collections ?? [], inbox_count: 0 }),
    ),
    http.get(`${API}/tags`, () => HttpResponse.json({ items: seed.tags ?? [] })),
    http.get(`${API}/resources`, () =>
      HttpResponse.json({
        items: seed.resources ?? [],
        page: 1,
        page_size: 20,
        total: seed.resources?.length ?? 0,
      }),
    ),
  )
}

/** Handlers for a browser that has no session: /me is 401, refresh fails, csrf works. */
export function signedOut() {
  server.use(
    http.get(`${API}/auth/csrf`, () => {
      setCsrfCookie()
      return new HttpResponse(null, { status: 204 })
    }),
    http.get(`${API}/me`, () => problem(401, 'unauthenticated')),
    http.post(`${API}/auth/refresh`, () => problem(401, 'unauthenticated')),
  )
}

/** Renders the real route tree at `path`, like the browser would. */
export function renderApp(path: string) {
  const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } })
  const router = createAppRouter(queryClient, createMemoryHistory({ initialEntries: [path] }))
  setSessionExpiredHandler(() => {
    queryClient.clear()
    if (!['/login', '/register'].includes(router.state.location.pathname)) {
      void router.navigate({ to: '/login', search: { next: router.state.location.href } })
    }
  })
  const view = render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>,
  )
  return { ...view, router, queryClient }
}
