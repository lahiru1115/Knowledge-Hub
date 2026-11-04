import { Pencil, RefreshCw, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
import { AiMark } from '@/features/ai/AiMark'
import { useGenerateSummary } from '@/features/ai/mutations'
import { UNAVAILABLE_TIP, useAi } from '@/features/ai/queries'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'
import { Textarea } from '@/components/ui/textarea'
import { notifyError } from '@/lib/toasts'
import type { Resource } from './types'

/** The label follows `summary_source` (Doc 03 §5.3, §10.4): AI output is always marked as such (FR-AI-11). */
function SourceLabel({ source }: { source: Resource['summary_source'] }) {
  if (source === 'ai')
    return (
      <span
        className="text-xs text-ai"
        title="Written by an AI model from this page's text. Edit it any time."
      >
        <AiMark /> AI-generated · may contain mistakes
      </span>
    )
  if (source === 'extracted')
    return <span className="text-xs text-muted-foreground">Extracted from the page</span>
  if (source === 'user')
    return <span className="text-xs text-muted-foreground">Written by you</span>
  return null
}

/**
 * The summary and where it came from. The user can generate one, regenerate it, write or edit it, and remove it.
 * While the AI service is down the generate buttons are disabled with the reason; the existing summary still shows.
 */
export function SummaryBlock({
  resource,
  onSave,
}: {
  resource: Resource
  onSave: (summary: string | null) => void
}) {
  const ai = useAi()
  const generate = useGenerateSummary()
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState(resource.summary ?? '')
  const [thin, setThin] = useState(false)
  const [confirmReplace, setConfirmReplace] = useState(false)

  function run() {
    setThin(false)
    generate.mutate(resource.id, {
      onError: (e) => {
        if (e instanceof ApiProblem && e.code === 'not_enough_content') setThin(true)
        else notifyError(e)
      },
    })
  }
  const busy = generate.isPending
  const disabledTip = ai.available ? undefined : UNAVAILABLE_TIP

  return (
    <section aria-labelledby="summary-title" aria-busy={busy || undefined} className="space-y-2">
      <div className="flex items-center justify-between gap-2">
        <h3
          id="summary-title"
          className="text-xs font-semibold tracking-wide text-muted-foreground uppercase"
        >
          Summary
        </h3>
        {resource.summary && !editing && (
          <div className="flex items-center gap-1">
            <SourceLabel source={resource.summary_source} />
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Regenerate summary"
              title={disabledTip ?? 'Regenerate'}
              disabled={!ai.available || busy}
              onClick={() => {
                if (resource.summary_source === 'user') setConfirmReplace(true)
                else run()
              }}
            >
              {busy ? <Spinner /> : <RefreshCw aria-hidden="true" />}
            </Button>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Edit summary"
              onClick={() => {
                setText(resource.summary ?? '')
                setEditing(true)
              }}
            >
              <Pencil aria-hidden="true" />
            </Button>
            <Button
              variant="ghost"
              size="icon-sm"
              aria-label="Remove summary"
              onClick={() => {
                onSave(null)
              }}
            >
              <Trash2 aria-hidden="true" />
            </Button>
          </div>
        )}
      </div>
      {editing ? (
        <div className="space-y-2">
          <Textarea
            aria-label="Summary"
            rows={4}
            maxLength={2000}
            value={text}
            onChange={(e) => {
              setText(e.target.value)
            }}
          />
          <div className="flex gap-2">
            <Button
              size="sm"
              onClick={() => {
                onSave(text.trim() || null)
                setEditing(false)
              }}
            >
              Save summary
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={() => {
                setEditing(false)
              }}
            >
              Cancel
            </Button>
          </div>
        </div>
      ) : resource.summary ? (
        <p className="rounded-md bg-ai-subtle p-3 text-sm text-foreground dark:bg-muted">
          {resource.summary}
        </p>
      ) : (
        <div className="space-y-2">
          <p className="text-sm text-muted-foreground">
            {thin ? messageFor('not_enough_content') : 'No summary yet.'}
          </p>
          <div className="flex flex-wrap gap-2">
            <Button
              size="sm"
              variant="outline"
              disabled={!ai.available || busy}
              title={disabledTip}
              onClick={run}
            >
              {busy ? <Spinner /> : <AiMark />} Summarise
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={() => {
                setText('')
                setEditing(true)
              }}
            >
              Write a summary
            </Button>
          </div>
          {!ai.available && !ai.loading && (
            <p className="text-xs text-muted-foreground">{UNAVAILABLE_TIP}</p>
          )}
        </div>
      )}
      <AlertDialog open={confirmReplace} onOpenChange={setConfirmReplace}>
        <AlertDialogContent>
          <AlertDialogHeader>
            <AlertDialogTitle>Replace your summary?</AlertDialogTitle>
            <AlertDialogDescription>
              You wrote this summary yourself. Regenerating replaces it with one made from the page.
            </AlertDialogDescription>
          </AlertDialogHeader>
          <AlertDialogFooter>
            <AlertDialogCancel>Keep mine</AlertDialogCancel>
            <AlertDialogAction onClick={run}>Replace it</AlertDialogAction>
          </AlertDialogFooter>
        </AlertDialogContent>
      </AlertDialog>
    </section>
  )
}
