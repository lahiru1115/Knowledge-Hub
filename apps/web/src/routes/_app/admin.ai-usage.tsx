import { createFileRoute } from '@tanstack/react-router'
import { AiUsagePage } from '@/features/admin/AiUsagePage'

export const Route = createFileRoute('/_app/admin/ai-usage')({ component: AiUsagePage })
