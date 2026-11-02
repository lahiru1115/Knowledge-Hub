import { Pencil, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Textarea } from '@/components/ui/textarea'
import type { Resource } from './types'

const LABELS = {
  none: '',
  ai: '✦ AI-generated',
  extracted: 'Extracted from the page',
  user: 'Written by you',
} as const

/**
 * The summary and where it came from (FR-AI-11). The user can write, edit and remove it. Generating one with AI
 * arrives with the AI service, so no "Summarise" button is shown yet.
 */
export function SummaryBlock({
  resource,
  onSave,
}: {
  resource: Resource
  onSave: (summary: string | null) => void
}) {
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState(resource.summary ?? '')

  return (
    <section aria-labelledby="summary-title" className="space-y-2">
      <div className="flex items-center justify-between gap-2">
        <h3
          id="summary-title"
          className="text-xs font-semibold tracking-wide text-muted-foreground uppercase"
        >
          Summary
        </h3>
        {resource.summary && !editing && (
          <div className="flex items-center gap-1">
            <span className="text-xs text-muted-foreground">{LABELS[resource.summary_source]}</span>
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
        <p className="rounded-md bg-muted p-3 text-sm">{resource.summary}</p>
      ) : (
        <div className="space-y-2">
          <p className="text-sm text-muted-foreground">No summary yet.</p>
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
      )}
    </section>
  )
}
