import { createFileRoute, redirect } from '@tanstack/react-router'
import { ApiProblem } from '@/api/client'
import { AppShell } from '@/features/auth/AppShell'
import { meQueryOptions } from '@/features/auth/session'

export const Route = createFileRoute('/_app')({
  // Everything inside is behind sign-in. A failed silent refresh sends the user to sign-in and brings
  // them back here afterwards (`next`).
  beforeLoad: async ({ context, location }) => {
    try {
      await context.queryClient.query({ ...meQueryOptions, staleTime: 'static' })
    } catch (e) {
      if (e instanceof ApiProblem && e.code === 'unauthenticated') {
        redirect({ to: '/login', search: { next: location.href }, throw: true })
      }
      throw e
    }
  },
  component: AppShell,
})
