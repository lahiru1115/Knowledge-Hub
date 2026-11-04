import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
import { FormAlert } from '@/components/FormAlert'
import { Field, FieldDescription, FieldLabel } from '@/components/ui/field'
import { Switch } from '@/components/ui/switch'
import { AiMark } from '@/features/ai/AiMark'
import { aiUsageQuery, useAi, type AiUsage } from '@/features/ai/queries'
import { meQueryOptions } from '@/features/auth/session'
import { timeAgo } from '@/lib/format'
import { useUpdateMe } from './useUpdateMe'

const OPERATIONS: Record<string, string> = {
  summarize: 'Summaries',
  suggest_tags: 'Tag suggestions',
  search: 'Searches by meaning',
  chat: 'Questions to Ask',
}

function UsageRow({ item }: { item: AiUsage['items'][number] }) {
  const name = OPERATIONS[item.operation] ?? item.operation
  return (
    <li className="space-y-1">
      <div className="flex items-baseline justify-between gap-2 text-sm">
        <span id={`use-${item.operation}`}>{name}</span>
        <span className="text-muted-foreground">
          {item.limit === null
            ? `${item.used.toString()} today, no limit`
            : `${item.used.toString()} of ${item.limit.toString()}`}
        </span>
      </div>
      {item.limit !== null && item.limit > 0 && (
        <meter
          aria-labelledby={`use-${item.operation}`}
          min={0}
          max={item.limit}
          value={Math.min(item.used, item.limit)}
          className="h-2 w-full"
        />
      )}
    </li>
  )
}

/** Settings → AI (Doc 03 §5.8): the two automatic features, today's use against the limits, and what the profile means. */
export function AiSection() {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const update = useUpdateMe()
  const ai = useAi()
  const usage = useQuery(aiUsageQuery)
  const [error, setError] = useState<string>()

  async function set(key: 'ai_auto_summary' | 'ai_tag_suggestions', value: boolean) {
    setError(undefined)
    try {
      await update.mutateAsync({ preferences: { [key]: value } })
    } catch (e) {
      setError(e instanceof ApiProblem ? messageFor(e.code, e) : messageFor('internal_error'))
    }
  }

  return (
    <section aria-labelledby="ai-title" className="max-w-md space-y-6">
      <h2 id="ai-title" className="text-xl font-semibold">
        AI
      </h2>

      <div className="space-y-4">
        <Field orientation="horizontal">
          <Switch
            id="auto-summary"
            checked={me.preferences.ai_auto_summary}
            disabled={update.isPending}
            onCheckedChange={(v) => void set('ai_auto_summary', v)}
            aria-describedby="auto-summary-hint"
          />
          <div className="space-y-1">
            <FieldLabel htmlFor="auto-summary">Summarise new resources automatically</FieldLabel>
            <FieldDescription id="auto-summary-hint">
              A summary is written after a link is saved. It never replaces one you wrote.
            </FieldDescription>
          </div>
        </Field>
        <Field orientation="horizontal">
          <Switch
            id="tag-suggestions"
            checked={me.preferences.ai_tag_suggestions}
            disabled={update.isPending}
            onCheckedChange={(v) => void set('ai_tag_suggestions', v)}
            aria-describedby="tag-suggestions-hint"
          />
          <div className="space-y-1">
            <FieldLabel htmlFor="tag-suggestions">Suggest tags when adding</FieldLabel>
            <FieldDescription id="tag-suggestions-hint">
              Suggestions are only offered. Nothing is added until you click it.
            </FieldDescription>
          </div>
        </Field>
        {error && <FormAlert>{error}</FormAlert>}
      </div>

      <div className="space-y-3">
        <h3 className="text-sm font-semibold">Today&apos;s use</h3>
        {usage.isPending ? (
          <p className="text-sm text-muted-foreground">Loading…</p>
        ) : usage.isError ? (
          <p className="text-sm text-muted-foreground">
            Usage is unavailable right now. {messageFor('ai_unavailable')}
          </p>
        ) : (
          <>
            <ul className="space-y-3">
              {usage.data.items.map((i) => (
                <UsageRow key={i.operation} item={i} />
              ))}
            </ul>
            <p className="text-xs text-muted-foreground">
              Limits reset {timeAgo(usage.data.reset_at)} (00:00 UTC).
            </p>
          </>
        )}
      </div>

      <div className="space-y-2">
        <h3 className="text-sm font-semibold">Mode</h3>
        <p className="text-sm">
          <AiMark />{' '}
          {ai.profile === 'claude' ? 'Claude' : ai.profile === 'free' ? 'Free' : 'Unknown'}
          {!ai.loading && !ai.available && ' (not reachable right now)'}
        </p>
        <p className="text-sm text-muted-foreground">
          Free mode runs on this server and costs nothing; summaries are extracts from the page.
          Claude mode uses Anthropic&apos;s API for better summaries and answers. Your data is never
          used to train models.
        </p>
      </div>
    </section>
  )
}
