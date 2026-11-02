import { createFileRoute } from '@tanstack/react-router'
import { AccountSection } from '@/features/settings/AccountSection'

export const Route = createFileRoute('/_app/settings/account/')({ component: AccountSection })
