import { useQueryClient } from '@tanstack/react-query'
import { useEffect, useRef, useState } from 'react'
import Markdown from 'react-markdown'
import rehypeSanitize from 'rehype-sanitize'
import remarkGfm from 'remark-gfm'
import { api, ApiProblem, unwrap } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Tabs, TabsContent, TabsList, TabsTrigger } from '@/components/ui/tabs'
import { Textarea } from '@/components/ui/textarea'
import { notifyError } from '@/lib/toasts'
import { keys } from './queries'
import type { Resource } from './types'

/**
 * shadcn's default inactive tab text is 60% foreground, about 4.3:1 on the tab bar and under AA. The design-system
 * muted colour is 7:1. Both panels stay mounted (hidden when inactive) so each tab's aria-controls points at a real element.
 */
const TAB_CLASS = 'text-muted-foreground data-[state=active]:text-foreground'

type Status = 'idle' | 'saving' | 'saved' | 'conflict' | 'error'
const AUTOSAVE_MS = 1000

/** Markdown rendered without raw HTML or scripts (FR-RES-08): react-markdown never emits HTML, and the result is sanitised too. */
export function SafeMarkdown({ children }: { children: string }) {
  return (
    <div className="prose-sm max-w-none space-y-2 text-sm [&_a]:text-primary [&_a]:underline [&_code]:rounded [&_code]:bg-muted [&_code]:px-1 [&_h1]:text-lg [&_h1]:font-semibold [&_h2]:font-semibold [&_li]:ml-5 [&_ol]:list-decimal [&_pre]:overflow-x-auto [&_pre]:rounded [&_pre]:bg-muted [&_pre]:p-2 [&_ul]:list-disc">
      <Markdown
        remarkPlugins={[remarkGfm]}
        rehypePlugins={[rehypeSanitize]}
        components={{
          a: ({ href, children }) => (
            <a href={href} target="_blank" rel="noopener noreferrer nofollow">
              {children}
            </a>
          ),
        }}
      >
        {children}
      </Markdown>
    </div>
  )
}

/**
 * Notes with Write and Preview. Saves 1 second after typing stops and on blur, shows "Saving… / Saved ✓", and
 * on 409 edit_conflict offers "Reload latest" or "Keep mine" (Doc 03 §5.3, Doc 02 §8.3).
 */
export function NotesEditor({ resource }: { resource: Resource }) {
  const qc = useQueryClient()
  const [text, setText] = useState(resource.notes ?? '')
  const [status, setStatus] = useState<Status>('idle')
  // What the server last had, and when: the base for the next save's expected_updated_at.
  const base = useRef({ notes: resource.notes ?? '', updatedAt: resource.updated_at })
  const latest = useRef(text)
  latest.current = text
  const saving = useRef(false)
  const saveRef = useRef<(force?: boolean) => Promise<void>>(() => Promise.resolve())

  // Follow the server when someone else's change arrives and I have nothing unsaved; never clobber typing.
  useEffect(() => {
    const server = resource.notes ?? ''
    if (latest.current === base.current.notes) {
      setText(server)
      latest.current = server
      base.current = { notes: server, updatedAt: resource.updated_at }
    } else if (server === base.current.notes) {
      base.current.updatedAt = resource.updated_at // an unrelated edit (favourite, tags) moved the timestamp
    }
  }, [resource.notes, resource.updated_at])

  async function save(force = false) {
    const value = latest.current
    if (saving.current || value === base.current.notes) return
    saving.current = true
    let succeeded = false
    setStatus('saving')
    try {
      const saved = await unwrap(
        api.PATCH('/resources/{id}', {
          params: { path: { id: resource.id } },
          body: {
            notes: value,
            ...(force ? {} : { expected_updated_at: base.current.updatedAt }),
          } as never,
        }),
      )
      base.current = { notes: saved.notes ?? '', updatedAt: saved.updated_at }
      qc.setQueryData(keys.resource(resource.id), saved)
      void qc.invalidateQueries({ queryKey: keys.resources })
      setStatus('saved')
      succeeded = true
    } catch (e) {
      if (e instanceof ApiProblem && e.code === 'edit_conflict') setStatus('conflict')
      else {
        setStatus('error')
        notifyError(e)
      }
    } finally {
      saving.current = false
    }
    // Typing continued while saving: save again (only after a success, so a failing save cannot loop).
    if (succeeded && latest.current !== base.current.notes) void save()
  }

  saveRef.current = save

  // Autosave after typing stops.
  useEffect(() => {
    if (text === base.current.notes) return
    const t = setTimeout(() => {
      void saveRef.current()
    }, AUTOSAVE_MS)
    return () => {
      clearTimeout(t)
    }
  }, [text])

  // Closing the drawer saves what is left.
  useEffect(() => {
    const flush = saveRef
    return () => {
      void flush.current()
    }
  }, [])

  async function reloadLatest() {
    const fresh = await unwrap(
      api.GET('/resources/{id}', { params: { path: { id: resource.id } } }),
    )
    qc.setQueryData(keys.resource(resource.id), fresh)
    base.current = { notes: fresh.notes ?? '', updatedAt: fresh.updated_at }
    setText(fresh.notes ?? '')
    setStatus('idle')
  }

  async function keepMine() {
    const fresh = await unwrap(
      api.GET('/resources/{id}', { params: { path: { id: resource.id } } }),
    )
    base.current = { notes: base.current.notes, updatedAt: fresh.updated_at }
    setStatus('idle')
    void save()
  }

  return (
    <section aria-labelledby="notes-title" className="space-y-2">
      <div className="flex items-center justify-between">
        <h3
          id="notes-title"
          className="text-xs font-semibold tracking-wide text-muted-foreground uppercase"
        >
          Notes
        </h3>
        <p role="status" className="text-xs text-muted-foreground">
          {status === 'saving' ? 'Saving…' : status === 'saved' ? 'Saved ✓' : ''}
        </p>
      </div>
      {status === 'conflict' && (
        <div role="alert" className="space-y-2 rounded-md border border-destructive p-3 text-sm">
          <p>These notes changed in another tab.</p>
          <div className="flex gap-2">
            <Button size="sm" variant="outline" onClick={() => void reloadLatest()}>
              Reload latest
            </Button>
            <Button size="sm" variant="outline" onClick={() => void keepMine()}>
              Keep mine
            </Button>
          </div>
        </div>
      )}
      <Tabs defaultValue="write">
        <TabsList>
          <TabsTrigger value="write" className={TAB_CLASS}>
            Write
          </TabsTrigger>
          <TabsTrigger value="preview" className={TAB_CLASS}>
            Preview
          </TabsTrigger>
        </TabsList>
        <TabsContent value="write" forceMount className="data-[state=inactive]:hidden">
          <Textarea
            aria-label="Notes (Markdown)"
            rows={8}
            maxLength={100_000}
            value={text}
            placeholder="Write notes in Markdown…"
            onChange={(e) => {
              setText(e.target.value)
            }}
            onBlur={() => void save()}
          />
        </TabsContent>
        <TabsContent value="preview" forceMount className="data-[state=inactive]:hidden">
          {text.trim() ? (
            <SafeMarkdown>{text}</SafeMarkdown>
          ) : (
            <p className="text-sm text-muted-foreground">Nothing to preview yet.</p>
          )}
        </TabsContent>
      </Tabs>
    </section>
  )
}
