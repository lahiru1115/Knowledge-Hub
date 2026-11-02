import { useQuery, useQueryClient } from '@tanstack/react-query'
import {
  ArchiveRestore,
  Archive,
  ExternalLink,
  Link2,
  MoreHorizontal,
  Pencil,
  Star,
  Trash2,
} from 'lucide-react'
import { useState } from 'react'
import { toast } from 'sonner'
import { api, ApiProblem, unwrap } from '@/api/client'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Field, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Separator } from '@/components/ui/separator'
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from '@/components/ui/sheet'
import { Skeleton } from '@/components/ui/skeleton'
import { siteOf, timeAgo } from '@/lib/format'
import { keepOpenForLocalEscape, useKeyShortcuts } from '@/lib/shortcuts'
import { notifyError, notifyUndo } from '@/lib/toasts'
import { cn } from '@/lib/utils'
import { CollectionSelect } from './CollectionSelect'
import { FetchStatus } from './FetchStatus'
import { invalidateLibrary, useDeleteResource, useUpdateResource } from './mutations'
import { NotesEditor } from './NotesEditor'
import { keys, resourceQuery } from './queries'
import { SummaryBlock } from './SummaryBlock'
import { TagInput } from './TagInput'
import {
  RESOURCE_TYPES,
  typeLabel,
  type Resource,
  type ResourceType,
  type ResourceUpdate,
} from './types'

interface Props {
  id: string
  /** The IDs on the current page, so J and K can step through them. */
  siblings: string[]
  onNavigate: (id: string) => void
  onClose: () => void
}

/** Resource detail drawer (SCR-DRAWER): a right-hand sheet on desktop, full screen on mobile. */
export function ResourceDrawer({ id, siblings, onNavigate, onClose }: Props) {
  const query = useQuery(resourceQuery(id))
  const index = siblings.indexOf(id)
  const step = (d: number) => {
    const next = siblings[index + d]
    if (index >= 0 && next) onNavigate(next)
  }
  useKeyShortcuts(
    {
      j: () => {
        step(1)
      },
      k: () => {
        step(-1)
      },
    },
    true,
    true,
  )

  return (
    <Sheet
      open
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <SheetContent
        side="right"
        className="w-full gap-0 overflow-y-auto sm:max-w-[480px]"
        onEscapeKeyDown={keepOpenForLocalEscape}
      >
        {query.isPending ? (
          <>
            <SheetHeader>
              <SheetTitle>Resource</SheetTitle>
              <SheetDescription className="sr-only">Loading</SheetDescription>
            </SheetHeader>
            <div className="space-y-3 p-4" aria-busy="true">
              <Skeleton className="h-6 w-3/4" />
              <Skeleton className="h-4 w-1/2" />
              <Skeleton className="h-24 w-full" />
            </div>
          </>
        ) : query.isError ? (
          <>
            <SheetHeader>
              <SheetTitle>Resource</SheetTitle>
              <SheetDescription className="sr-only">Not available</SheetDescription>
            </SheetHeader>
            <div role="alert" className="space-y-3 p-4">
              <p>
                {query.error instanceof ApiProblem && query.error.code === 'not_found'
                  ? "This resource doesn't exist or was deleted."
                  : "Couldn't load this resource."}
              </p>
              <Button variant="outline" onClick={onClose}>
                Close
              </Button>
            </div>
          </>
        ) : (
          <DrawerBody resource={query.data} onClose={onClose} />
        )}
      </SheetContent>
    </Sheet>
  )
}

function DrawerBody({ resource, onClose }: { resource: Resource; onClose: () => void }) {
  const qc = useQueryClient()
  const update = useUpdateResource()
  const remove = useDeleteResource()
  const [editingTitle, setEditingTitle] = useState(false)
  const [title, setTitle] = useState(resource.title ?? '')
  const [editingUrl, setEditingUrl] = useState(false)
  const [url, setUrl] = useState(resource.url)
  const [urlError, setUrlError] = useState<string>()

  async function patch(body: ResourceUpdate, opts: { quiet?: boolean } = {}) {
    try {
      const saved = await unwrap(
        api.PATCH('/resources/{id}', { params: { path: { id: resource.id } }, body }),
      )
      qc.setQueryData(keys.resource(resource.id), saved)
      void invalidateLibrary(qc)
      return saved
    } catch (e) {
      if (!opts.quiet) notifyError(e)
      throw e
    }
  }

  const displayTitle = resource.title ?? resource.url
  const commitTitle = () => {
    setEditingTitle(false)
    const next = title.trim()
    if (next !== (resource.title ?? ''))
      void patch({ title: next || null }).catch(() => {
        setTitle(resource.title ?? '')
      })
  }

  return (
    <>
      <SheetHeader className="flex-row items-start justify-between gap-2 border-b border-border">
        <div className="min-w-0">
          <SheetTitle>Resource</SheetTitle>
          <SheetDescription className="sr-only">Details for {displayTitle}</SheetDescription>
        </div>
        <div className="mr-8 flex items-center gap-1">
          <Button asChild variant="ghost" size="sm">
            <a href={resource.url} target="_blank" rel="noopener noreferrer">
              <ExternalLink aria-hidden="true" /> Open
            </a>
          </Button>
          <DropdownMenu>
            <DropdownMenuTrigger asChild>
              <Button variant="ghost" size="icon-sm" aria-label="More actions">
                <MoreHorizontal aria-hidden="true" />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent align="end">
              <DropdownMenuItem
                onSelect={() => {
                  update.mutate({ id: resource.id, body: { is_archived: !resource.is_archived } })
                }}
              >
                {resource.is_archived ? <ArchiveRestore /> : <Archive />}{' '}
                {resource.is_archived ? 'Unarchive' : 'Archive'}
              </DropdownMenuItem>
              <DropdownMenuItem
                onSelect={() => {
                  void navigator.clipboard.writeText(resource.url).then(() => toast('Link copied'))
                }}
              >
                <Link2 /> Copy link
              </DropdownMenuItem>
              <DropdownMenuSeparator />
              <DropdownMenuItem
                variant="destructive"
                onSelect={() => {
                  remove.mutate(resource, { onSuccess: onClose })
                }}
              >
                <Trash2 /> Delete
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </div>
      </SheetHeader>

      <div className="space-y-5 p-4">
        <div className="space-y-1">
          {editingTitle ? (
            <Input
              aria-label="Title"
              data-escape-local
              value={title}
              maxLength={300}
              onChange={(e) => {
                setTitle(e.target.value)
              }}
              onBlur={commitTitle}
              onKeyDown={(e) => {
                if (e.key === 'Enter') commitTitle()
                if (e.key === 'Escape') {
                  e.stopPropagation()
                  setTitle(resource.title ?? '')
                  setEditingTitle(false)
                }
              }}
            />
          ) : (
            <div className="flex items-start gap-1">
              <h2 className="min-w-0 flex-1 text-lg font-semibold break-words">{displayTitle}</h2>
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label="Edit title"
                onClick={() => {
                  setTitle(resource.title ?? '')
                  setEditingTitle(true)
                }}
              >
                <Pencil aria-hidden="true" />
              </Button>
            </div>
          )}
          <p className="flex flex-wrap items-center gap-x-2 text-sm text-muted-foreground">
            <span>{siteOf(resource.url)}</span>
            <span aria-hidden="true">·</span>
            <span>{typeLabel(resource.type)}</span>
            <span aria-hidden="true">·</span>
            <span>Saved {timeAgo(resource.created_at)}</span>
            <Button
              variant="ghost"
              size="icon-sm"
              className="ml-auto"
              aria-label={resource.is_favourite ? 'Remove from favourites' : 'Add to favourites'}
              aria-pressed={resource.is_favourite}
              onClick={() => {
                update.mutate({ id: resource.id, body: { is_favourite: !resource.is_favourite } })
              }}
            >
              <Star
                className={cn(resource.is_favourite && 'fill-current text-warning')}
                aria-hidden="true"
              />
            </Button>
          </p>
        </div>

        <FetchStatus resource={resource} />
        {resource.description && <p className="text-sm">{resource.description}</p>}

        <div className="space-y-2">
          {editingUrl ? (
            <Field data-invalid={urlError ? true : undefined}>
              <FieldLabel htmlFor="drawer-url">Link</FieldLabel>
              <Input
                id="drawer-url"
                value={url}
                aria-invalid={urlError ? true : undefined}
                onChange={(e) => {
                  setUrl(e.target.value)
                }}
              />
              {urlError && (
                <p role="alert" className="text-sm text-destructive">
                  {urlError}
                </p>
              )}
              <div className="flex gap-2">
                <Button
                  size="sm"
                  onClick={() => {
                    setUrlError(undefined)
                    void patch({ url: url.trim() }, { quiet: true })
                      .then(() => {
                        setEditingUrl(false)
                        toast('Link changed. Details will be fetched again.')
                      })
                      .catch((e: unknown) => {
                        setUrlError(
                          e instanceof ApiProblem
                            ? (e.errors.url?.[0] ?? e.message)
                            : 'Could not save the link.',
                        )
                      })
                  }}
                >
                  Save link
                </Button>
                <Button
                  size="sm"
                  variant="outline"
                  onClick={() => {
                    setUrl(resource.url)
                    setUrlError(undefined)
                    setEditingUrl(false)
                  }}
                >
                  Cancel
                </Button>
              </div>
            </Field>
          ) : (
            <p className="flex items-center gap-1 text-sm">
              <a
                href={resource.url}
                target="_blank"
                rel="noopener noreferrer"
                className="min-w-0 flex-1 truncate font-mono text-xs text-primary underline"
                title={resource.url}
              >
                {resource.url}
              </a>
              <Button
                variant="ghost"
                size="icon-sm"
                aria-label="Edit link"
                onClick={() => {
                  setUrl(resource.url)
                  setEditingUrl(true)
                }}
              >
                <Pencil aria-hidden="true" />
              </Button>
            </p>
          )}
        </div>

        <div className="grid gap-4 sm:grid-cols-2">
          <Field>
            <FieldLabel htmlFor="drawer-collection">Collection</FieldLabel>
            <CollectionSelect
              id="drawer-collection"
              value={resource.collection_id}
              onChange={(collection_id) => {
                update.mutate({ id: resource.id, body: { collection_id } })
              }}
            />
          </Field>
          <Field>
            <FieldLabel htmlFor="drawer-type">Type</FieldLabel>
            <Select
              value={resource.type}
              onValueChange={(v) => {
                void patch({ type: v as ResourceType }).catch(() => undefined)
              }}
            >
              <SelectTrigger id="drawer-type" className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {RESOURCE_TYPES.map((t) => (
                  <SelectItem key={t.value} value={t.value}>
                    {t.label}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        </div>

        <TagInput
          value={resource.tags}
          onChange={(tags) => {
            void patch({ tags }).catch(() => undefined)
          }}
        />

        <Separator />
        <SummaryBlock
          resource={resource}
          onSave={(summary) => {
            const previous = resource.summary
            void patch({ summary })
              .then(() => {
                if (summary === null && previous)
                  notifyUndo('Summary removed', () => {
                    void patch({ summary: previous }).catch(() => undefined)
                  })
              })
              .catch(() => undefined)
          }}
        />
        <Separator />
        <NotesEditor key={resource.id} resource={resource} />
      </div>
    </>
  )
}
