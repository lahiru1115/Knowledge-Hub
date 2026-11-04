import { createFileRoute } from '@tanstack/react-router'
import { AiSection } from '@/features/settings/AiSection'

export const Route = createFileRoute('/_app/settings/ai')({ component: AiSection })
