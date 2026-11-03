import { createFileRoute, redirect } from '@tanstack/react-router'

export const Route = createFileRoute('/_app/admin/')({
  beforeLoad: () => {
    redirect({ to: '/admin/users', replace: true, throw: true })
  },
})
