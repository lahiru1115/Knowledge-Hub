import { createFileRoute } from '@tanstack/react-router'
import { DataSection } from '@/features/settings/DataSection'

export const Route = createFileRoute('/_app/settings/data')({ component: DataSection })
