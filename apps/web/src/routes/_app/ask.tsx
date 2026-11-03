import { createFileRoute } from '@tanstack/react-router'
import { AskPage } from '@/features/ask/AskPage'

// A layout route that never shows its children: both /ask and /ask/$sessionId render the same page, so it stays
// mounted (and keeps an answer that is still streaming) when a new chat gets its own address.
export const Route = createFileRoute('/_app/ask')({ component: AskPage })
