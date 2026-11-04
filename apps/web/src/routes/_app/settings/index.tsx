import { createFileRoute, redirect } from '@tanstack/react-router'

export const Route = createFileRoute('/_app/settings/')({
  beforeLoad: () => {
    redirect({ to: '/settings/profile', throw: true })
  },
})
