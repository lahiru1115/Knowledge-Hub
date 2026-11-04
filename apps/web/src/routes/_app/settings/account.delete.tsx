import { createFileRoute } from '@tanstack/react-router'
import { DeleteAccountPage } from '@/features/settings/DeleteAccountPage'

export const Route = createFileRoute('/_app/settings/account/delete')({
  component: DeleteAccountPage,
})
