import { createFileRoute, redirect } from '@tanstack/react-router'
import { z } from 'zod'
import { RegisterPage } from '@/features/auth/RegisterPage'
import { meQueryOptions } from '@/features/auth/session'
import { safeNext } from '@/lib/safeNext'

export const Route = createFileRoute('/register')({
  validateSearch: z.object({ next: z.string().optional() }),
  beforeLoad: async ({ context, search }) => {
    const me = await context.queryClient.query(meQueryOptions).catch(() => undefined)
    if (me) redirect({ href: safeNext(search.next), throw: true })
  },
  component: RegisterPage,
})
