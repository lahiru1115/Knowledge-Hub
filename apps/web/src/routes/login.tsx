import { createFileRoute, redirect } from '@tanstack/react-router'
import { z } from 'zod'
import { LoginPage } from '@/features/auth/LoginPage'
import { meQueryOptions } from '@/features/auth/session'
import { safeNext } from '@/lib/safeNext'

export const Route = createFileRoute('/login')({
  validateSearch: z.object({ next: z.string().optional() }),
  // Someone who is still signed in (or can be silently refreshed) skips the form.
  beforeLoad: async ({ context, search }) => {
    const me = await context.queryClient.query(meQueryOptions).catch(() => undefined)
    if (me) redirect({ href: safeNext(search.next), throw: true })
  },
  component: LoginPage,
})
