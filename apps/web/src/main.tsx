import { QueryClient, QueryClientProvider } from '@tanstack/react-query'
import { RouterProvider } from '@tanstack/react-router'
import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { setSessionExpiredHandler } from '@/api/client'
import './styles/globals.css'
import { createAppRouter } from './router'

const queryClient = new QueryClient({
  defaultOptions: { queries: { staleTime: 30_000, refetchOnWindowFocus: true } },
})
const router = createAppRouter(queryClient)

// A failed silent refresh means the session is over: drop cached data and go to sign-in (Doc 03 §12.4).
setSessionExpiredHandler(() => {
  queryClient.clear()
  const { pathname, href } = router.state.location
  if (pathname !== '/login' && pathname !== '/register') {
    void router.navigate({ to: '/login', search: { next: href } })
  }
})

const root = document.getElementById('root')
if (!root) throw new Error('Missing #root element')
createRoot(root).render(
  <StrictMode>
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  </StrictMode>,
)
