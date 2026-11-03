import { useQuery, useQueryClient } from '@tanstack/react-query'
import { useNavigate } from '@tanstack/react-router'
import { ChevronRight } from 'lucide-react'
import { createContext, useCallback, useContext, useMemo, useState, type ReactNode } from 'react'
import { toast } from 'sonner'
import { api, unwrap } from '@/api/client'
import { SuggestedTags } from '@/features/ai/SuggestedTags'
import { useAi } from '@/features/ai/queries'
import { LoadingButton } from '@/components/loading-button'
import { Alert, AlertDescription } from '@/components/ui/alert'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Field, FieldError, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { Textarea } from '@/components/ui/textarea'
import { keepOpenForLocalEscape } from '@/lib/shortcuts'
import { useDebounced } from '@/lib/useDebounced'
import { useIsDesktop } from '@/lib/useMediaQuery'
import { notifyError, UNDO_MS } from '@/lib/toasts'
import { CollectionSelect } from './CollectionSelect'
import { useCreateResource, invalidateLibrary } from './mutations'
import { collectionsQuery } from './queries'
import { TagInput } from './TagInput'
import { RESOURCE_TYPES, typeLabel, type ResourceType } from './types'

const AUTO = 'auto'

interface Initial {
  url?: string
  collectionId?: string | null
}

const QuickAddContext = createContext<{ open: (initial?: Initial) => void }>({
  open: () => undefined,
})
export const useQuickAdd = () => useContext(QuickAddContext)

/** Hosts the quick-add dialog so the Add button, `N`, empty states and collection pages can all open it. */
export function QuickAddProvider({ children }: { children: ReactNode }) {
  const [state, setState] = useState<{ open: boolean; initial: Initial }>({
    open: false,
    initial: {},
  })
  const open = useCallback((initial: Initial = {}) => {
    setState({ open: true, initial })
  }, [])
  const value = useMemo(() => ({ open }), [open])
  return (
    <QuickAddContext.Provider value={value}>
      {children}
      {state.open && (
        <QuickAddDialog
          initial={state.initial}
          onClose={() => {
            setState((s) => ({ ...s, open: false }))
          }}
        />
      )}
    </QuickAddContext.Provider>
  )
}

function looksLikeUrl(text: string): boolean {
  try {
    const u = new URL(text.trim())
    return u.protocol === 'http:' || u.protocol === 'https:'
  } catch {
    return false
  }
}

function QuickAddDialog({ initial, onClose }: { initial: Initial; onClose: () => void }) {
  const desktop = useIsDesktop()
  const body = <QuickAddForm initial={initial} onClose={onClose} />
  const onOpenChange = (o: boolean) => {
    if (!o) onClose()
  }
  if (desktop) {
    return (
      <Dialog open onOpenChange={onOpenChange}>
        <DialogContent
          className="max-h-[90dvh] overflow-y-auto sm:max-w-[560px]"
          onEscapeKeyDown={keepOpenForLocalEscape}
        >
          <DialogHeader>
            <DialogTitle>Add a resource</DialogTitle>
            <DialogDescription>
              Paste a link. Add tags and a collection if you like.
            </DialogDescription>
          </DialogHeader>
          {body}
        </DialogContent>
      </Dialog>
    )
  }
  return (
    <Sheet open onOpenChange={onOpenChange}>
      <SheetContent side="bottom" className="max-h-[92dvh] overflow-y-auto px-4 pb-6">
        <SheetHeader>
          <SheetTitle>Add a resource</SheetTitle>
          <SheetDescription>Paste a link. Add tags and a collection if you like.</SheetDescription>
        </SheetHeader>
        {body}
      </SheetContent>
    </Sheet>
  )
}

/** What the link looks like, fetched while the user types (FR-RES-01). Saving never waits for it. */
function PreviewCard({ url, current }: { url: string; current: string }) {
  const enabled = looksLikeUrl(url)
  const preview = useQuery({
    queryKey: ['preview', url],
    queryFn: () => unwrap(api.POST('/metadata/preview', { body: { url } })),
    enabled,
    retry: false,
    staleTime: 5 * 60_000,
  })
  // Hidden while the user is still typing a different address, so a stale card is never shown.
  if (!enabled || url !== current) return null
  if (preview.isPending)
    return (
      <div
        role="status"
        aria-busy="true"
        aria-label="Loading a preview"
        className="flex gap-3 rounded-md border border-border p-3"
      >
        <Skeleton className="size-12 shrink-0 rounded" />
        <div className="flex-1 space-y-2">
          <Skeleton className="h-4 w-3/4" />
          <Skeleton className="h-3 w-full" />
        </div>
      </div>
    )
  const p = preview.data
  if (preview.isError || !p || p.error || !(p.title ?? p.description))
    return (
      <p className="text-sm text-muted-foreground">
        Couldn't load a preview. You can still save it.
      </p>
    )
  return (
    <div
      className="flex gap-3 rounded-md border border-border p-3"
      aria-label="Preview"
      role="group"
    >
      {p.image_url && (
        <img
          src={p.image_url}
          alt=""
          className="size-12 shrink-0 rounded object-cover"
          loading="lazy"
        />
      )}
      <div className="min-w-0 flex-1">
        {p.title && <p className="truncate text-sm font-medium">{p.title}</p>}
        {p.description && (
          <p className="line-clamp-2 text-sm text-muted-foreground">{p.description}</p>
        )}
        {p.type && <p className="mt-1 text-xs text-muted-foreground">{typeLabel(p.type)}</p>}
      </div>
    </div>
  )
}

function QuickAddForm({ initial, onClose }: { initial: Initial; onClose: () => void }) {
  const qc = useQueryClient()
  const navigate = useNavigate()
  const create = useCreateResource()
  const ai = useAi()
  const { data: collections } = useQuery(collectionsQuery)

  const [url, setUrl] = useState(initial.url ?? '')
  const [touched, setTouched] = useState(false)
  const [tags, setTags] = useState<string[]>([])
  const [collectionId, setCollectionId] = useState<string | null>(initial.collectionId ?? null)
  const [details, setDetails] = useState(false)
  const [title, setTitle] = useState('')
  // Sent only when the user picks one: otherwise the fetched details decide (FR-META-02).
  const [type, setType] = useState<ResourceType | typeof AUTO>(AUTO)
  const [notes, setNotes] = useState('')
  const [dismissedDuplicate, setDismissedDuplicate] = useState(false)

  const valid = looksLikeUrl(url)
  const debouncedUrl = useDebounced(url.trim(), 400)
  const duplicate = useQuery({
    queryKey: ['check-url', debouncedUrl],
    queryFn: () =>
      unwrap(api.GET('/resources/check-url', { params: { query: { url: debouncedUrl } } })),
    enabled: looksLikeUrl(debouncedUrl),
    retry: false,
  })
  const existing = valid && debouncedUrl === url.trim() ? duplicate.data?.existing : undefined
  const showDuplicate = existing != null && !dismissedDuplicate
  const urlError =
    touched && url.trim() !== '' && !valid
      ? 'Enter a link that starts with http:// or https://'
      : undefined

  async function save() {
    setTouched(true)
    if (!valid) return
    try {
      const saved = await create.mutateAsync({
        url: url.trim(),
        tags,
        collection_id: collectionId,
        ...(title.trim() ? { title: title.trim() } : {}),
        ...(notes.trim() ? { notes } : {}),
        ...(type === AUTO ? {} : { type }),
      })
      const where = collectionId
        ? (collections?.items.find((c) => c.id === collectionId)?.name ?? 'collection')
        : 'Inbox'
      toast(`Saved to ${where}`, {
        duration: UNDO_MS,
        action: {
          label: 'Undo',
          onClick: () => {
            void unwrap(api.DELETE('/resources/{id}', { params: { path: { id: saved.id } } }))
              .then(() => invalidateLibrary(qc))
              .catch(notifyError)
          },
        },
        cancel: {
          label: 'Open',
          onClick: () => {
            void navigate({ to: '/library', search: { r: saved.id } })
          },
        },
      })
      onClose()
    } catch (e) {
      notifyError(e) // the dialog stays open with everything typed so far (Doc 03 §5.1)
    }
  }

  return (
    <form
      className="space-y-4"
      noValidate
      onSubmit={(e) => {
        e.preventDefault()
        if (!showDuplicate) void save()
      }}
    >
      <Field data-invalid={urlError ? true : undefined}>
        <FieldLabel htmlFor="qa-url">URL</FieldLabel>
        <Input
          id="qa-url"
          type="text"
          inputMode="url"
          autoComplete="off"
          placeholder="https://"
          value={url}
          aria-invalid={urlError ? true : undefined}
          aria-describedby={urlError ? 'qa-url-error' : undefined}
          onChange={(e) => {
            setUrl(e.target.value)
            setDismissedDuplicate(false)
          }}
          onBlur={() => {
            setTouched(true)
          }}
        />
        {urlError && <FieldError id="qa-url-error">{urlError}</FieldError>}
      </Field>

      {showDuplicate && (
        <Alert>
          <AlertDescription className="space-y-2">
            <p>
              You saved this on{' '}
              {new Date(existing.created_at).toLocaleDateString('en', {
                day: 'numeric',
                month: 'short',
              })}
              {existing.collection_id
                ? ` in ${collections?.items.find((c) => c.id === existing.collection_id)?.name ?? 'a collection'}`
                : ' in the Inbox'}
              .
            </p>
            <div className="flex gap-2">
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() => {
                  onClose()
                  void navigate({ to: '/library', search: { r: existing.id } })
                }}
              >
                Open it
              </Button>
              <Button
                type="button"
                size="sm"
                variant="outline"
                onClick={() => {
                  setDismissedDuplicate(true)
                }}
              >
                Save anyway
              </Button>
            </div>
          </AlertDescription>
        </Alert>
      )}

      {!showDuplicate && <PreviewCard url={debouncedUrl} current={url.trim()} />}

      <TagInput value={tags} onChange={setTags} />
      {valid && debouncedUrl === url.trim() && (
        <SuggestedTags
          url={debouncedUrl}
          current={tags}
          enabled={ai.tagSuggestions}
          onAccept={(names) => {
            setTags((t) => [...new Set([...t, ...names])])
          }}
        />
      )}

      <Field>
        <FieldLabel htmlFor="qa-collection">Collection</FieldLabel>
        <CollectionSelect id="qa-collection" value={collectionId} onChange={setCollectionId} />
      </Field>

      <div>
        <button
          type="button"
          aria-expanded={details}
          aria-controls="qa-details"
          className="flex items-center gap-1 text-sm font-medium text-muted-foreground hover:text-foreground"
          onClick={() => {
            setDetails((d) => !d)
          }}
        >
          <ChevronRight
            className={`size-4 transition-transform ${details ? 'rotate-90' : ''}`}
            aria-hidden="true"
          />
          More details (title, type, notes)
        </button>
        {details && (
          <div id="qa-details" className="mt-3 space-y-4">
            <Field>
              <FieldLabel htmlFor="qa-title">Title</FieldLabel>
              <Input
                id="qa-title"
                value={title}
                maxLength={300}
                onChange={(e) => {
                  setTitle(e.target.value)
                }}
              />
            </Field>
            <Field>
              <FieldLabel htmlFor="qa-type">Type</FieldLabel>
              <Select
                value={type}
                onValueChange={(v) => {
                  setType(v as ResourceType | typeof AUTO)
                }}
              >
                <SelectTrigger id="qa-type" className="w-full">
                  <SelectValue />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem value={AUTO}>Detect automatically</SelectItem>
                  {RESOURCE_TYPES.map((t) => (
                    <SelectItem key={t.value} value={t.value}>
                      {t.label}
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>
            </Field>
            <Field>
              <FieldLabel htmlFor="qa-notes">Notes</FieldLabel>
              <Textarea
                id="qa-notes"
                rows={4}
                value={notes}
                onChange={(e) => {
                  setNotes(e.target.value)
                }}
              />
            </Field>
          </div>
        )}
      </div>

      <DialogFooter className="gap-2 sm:gap-2">
        <Button type="button" variant="outline" onClick={onClose}>
          Cancel
        </Button>
        <LoadingButton
          type="submit"
          loading={create.isPending}
          disabled={!valid || showDuplicate || create.isPending}
        >
          Save
        </LoadingButton>
      </DialogFooter>
    </form>
  )
}
