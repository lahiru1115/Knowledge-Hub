import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { GitMerge, MoreHorizontal, Trash2 } from 'lucide-react'
import { useMemo, useState } from 'react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
import { LoadingButton } from '@/components/loading-button'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
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
import { Skeleton } from '@/components/ui/skeleton'
import { normalizeTag } from '@/features/library/TagInput'
import { tagsQuery } from '@/features/library/queries'
import type { Tag } from '@/features/library/types'
import { plural } from '@/lib/format'
import { notify, notifyError } from '@/lib/toasts'
import { usePageTitle } from '@/lib/usePageTitle'
import { similarTo } from './similar'
import { useDeleteTags, useMergeTag, useRenameTag } from './tagMutations'

type Order = 'used' | 'name'

/** Tags manager (SCR-TAGS): rename inline, merge, delete with Undo. */
export function TagsPage() {
  usePageTitle('Tags')
  const { data, isPending, isError, refetch } = useQuery(tagsQuery)
  const [filter, setFilter] = useState('')
  const [order, setOrder] = useState<Order>('used')
  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [renaming, setRenaming] = useState<string | null>(null)
  const [merging, setMerging] = useState<Tag[] | null>(null)
  const merge = useMergeTag()
  const del = useDeleteTags()

  const all = useMemo(() => data?.items ?? [], [data])
  const visible = useMemo(
    () =>
      all
        .filter((t) => t.name.includes(filter.trim().toLowerCase()))
        .sort((a, b) =>
          order === 'used'
            ? b.resource_count - a.resource_count || a.name.localeCompare(b.name)
            : a.name.localeCompare(b.name),
        ),
    [all, filter, order],
  )
  const chosen = all.filter((t) => selected.has(t.id))

  return (
    <div className="space-y-4">
      <header className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-2xl font-semibold">Tags</h1>
        <div className="flex flex-wrap items-center gap-2">
          <Input
            aria-label="Filter tags"
            placeholder="Filter tags…"
            className="w-48"
            value={filter}
            onChange={(e) => {
              setFilter(e.target.value)
            }}
          />
          <Select
            value={order}
            onValueChange={(v) => {
              setOrder(v as Order)
            }}
          >
            <SelectTrigger aria-label="Sort tags" className="w-40">
              <SelectValue />
            </SelectTrigger>
            <SelectContent>
              <SelectItem value="used">Sort: Most used</SelectItem>
              <SelectItem value="name">Sort: A–Z</SelectItem>
            </SelectContent>
          </Select>
        </div>
      </header>

      {chosen.length > 0 && (
        <div
          role="toolbar"
          aria-label="Bulk actions"
          className="flex flex-wrap items-center gap-2 rounded-md border bg-muted px-3 py-2"
        >
          <span className="text-sm font-medium" aria-live="polite">
            {chosen.length} selected
          </span>
          <Button
            size="sm"
            variant="outline"
            onClick={() => {
              setMerging(chosen)
            }}
          >
            Merge into…
          </Button>
          <Button
            size="sm"
            variant="outline"
            className="text-destructive hover:text-destructive"
            onClick={() => {
              del.mutate(chosen, {
                onSuccess: () => {
                  setSelected(new Set())
                },
              })
            }}
          >
            Delete
          </Button>
          <Button
            size="sm"
            variant="ghost"
            className="ml-auto"
            onClick={() => {
              setSelected(new Set())
            }}
          >
            Cancel
          </Button>
        </div>
      )}

      {isPending ? (
        <ul aria-busy="true" className="divide-y rounded-md border">
          {[0, 1, 2, 3].map((i) => (
            <li key={i} className="p-4">
              <Skeleton className="h-4 w-1/3" />
            </li>
          ))}
        </ul>
      ) : isError ? (
        <div
          role="alert"
          className="space-y-3 rounded-md border border-destructive p-6 text-center"
        >
          <p className="font-medium">Couldn&apos;t load your tags.</p>
          <Button variant="outline" onClick={() => void refetch()}>
            Try again
          </Button>
        </div>
      ) : all.length === 0 ? (
        <div className="space-y-1 rounded-md border p-8 text-center">
          <p className="font-medium">No tags yet</p>
          <p className="text-muted-foreground">Tags appear when you add them to resources.</p>
        </div>
      ) : visible.length === 0 ? (
        <p className="rounded-md border p-6 text-center text-muted-foreground">
          No tags match &lsquo;{filter}&rsquo;.
        </p>
      ) : (
        <ul aria-label="Tags" className="rounded-md border">
          {visible.map((t) => {
            const twin = similarTo(t, all)
            return (
              <li
                key={t.id}
                className="flex flex-wrap items-center gap-3 border-b border-border px-3 py-2 last:border-b-0"
              >
                <Checkbox
                  aria-label={`Select #${t.name}`}
                  checked={selected.has(t.id)}
                  onCheckedChange={(c) => {
                    setSelected((s) => {
                      const n = new Set(s)
                      if (c === true) n.add(t.id)
                      else n.delete(t.id)
                      return n
                    })
                  }}
                />
                {renaming === t.id ? (
                  <RenameForm
                    tag={t}
                    onDone={() => {
                      setRenaming(null)
                    }}
                  />
                ) : (
                  <>
                    <Link
                      to="/library"
                      search={{ tag: [t.name] }}
                      className="font-medium hover:underline"
                    >
                      #{t.name}
                    </Link>
                    <span className="text-sm text-muted-foreground">
                      {plural(t.resource_count, 'resource')}
                    </span>
                    {twin && (
                      <span className="flex items-center gap-1 text-sm text-muted-foreground">
                        Similar to #{twin.name} →
                        <Button
                          size="sm"
                          variant="outline"
                          onClick={() => {
                            merge.mutate(
                              { id: t.id, target: twin.id },
                              {
                                onSuccess: () => {
                                  notify(`Merged #${t.name} into #${twin.name}`)
                                },
                                onError: (e) => {
                                  notifyError(e)
                                },
                              },
                            )
                          }}
                        >
                          Merge
                        </Button>
                      </span>
                    )}
                    <span className="ml-auto flex items-center gap-1">
                      <Button
                        size="sm"
                        variant="ghost"
                        onClick={() => {
                          setRenaming(t.id)
                        }}
                      >
                        Rename
                      </Button>
                      <DropdownMenu>
                        <DropdownMenuTrigger asChild>
                          <Button
                            variant="ghost"
                            size="icon-sm"
                            aria-label={`More actions for #${t.name}`}
                          >
                            <MoreHorizontal aria-hidden="true" />
                          </Button>
                        </DropdownMenuTrigger>
                        <DropdownMenuContent align="end">
                          <DropdownMenuItem
                            onSelect={() => {
                              setMerging([t])
                            }}
                          >
                            <GitMerge /> Merge into…
                          </DropdownMenuItem>
                          <DropdownMenuItem
                            variant="destructive"
                            onSelect={() => {
                              del.mutate([t])
                            }}
                          >
                            <Trash2 /> Delete
                          </DropdownMenuItem>
                        </DropdownMenuContent>
                      </DropdownMenu>
                    </span>
                  </>
                )}
              </li>
            )
          })}
        </ul>
      )}

      {merging && (
        <MergeDialog
          sources={merging}
          all={all}
          onClose={() => {
            setMerging(null)
            setSelected(new Set())
          }}
        />
      )}
    </div>
  )
}

/** Inline rename. A name that is already taken offers to merge instead (409 tag_name_taken, FR-TAG-03). */
function RenameForm({ tag, onDone }: { tag: Tag; onDone: () => void }) {
  const rename = useRenameTag()
  const merge = useMergeTag()
  const [name, setName] = useState(tag.name)
  const [error, setError] = useState<string>()
  const [mergeInto, setMergeInto] = useState<string>()

  function submit() {
    const next = normalizeTag(name)
    if (!next) {
      setError('Use letters, digits, spaces and - _ . # + (up to 50 characters).')
      return
    }
    if (next === tag.name) {
      onDone()
      return
    }
    setError(undefined)
    setMergeInto(undefined)
    rename.mutate(
      { id: tag.id, name: next },
      {
        onSuccess: onDone,
        onError: (e) => {
          if (e instanceof ApiProblem && e.code === 'tag_name_taken') {
            setError(`You already have #${next}. Merge the two tags instead?`)
            setMergeInto(e.suggestMergeTagId)
          } else
            setError(e instanceof ApiProblem ? messageFor(e.code, e) : messageFor('internal_error'))
        },
      },
    )
  }

  return (
    <form
      className="flex flex-1 flex-wrap items-center gap-2"
      onSubmit={(e) => {
        e.preventDefault()
        submit()
      }}
    >
      <Input
        aria-label={`New name for #${tag.name}`}
        value={name}
        className="w-56"
        aria-invalid={error ? true : undefined}
        aria-describedby={error ? `rename-${tag.id}-error` : undefined}
        onChange={(e) => {
          setName(e.target.value)
        }}
        onKeyDown={(e) => {
          if (e.key === 'Escape') {
            e.stopPropagation()
            onDone()
          }
        }}
      />
      <LoadingButton type="submit" size="sm" loading={rename.isPending}>
        Save
      </LoadingButton>
      <Button type="button" size="sm" variant="outline" onClick={onDone}>
        Cancel
      </Button>
      {error && (
        <p
          id={`rename-${tag.id}-error`}
          role="alert"
          className="flex w-full items-center gap-2 text-sm text-destructive"
        >
          {error}
          {mergeInto && (
            <Button
              type="button"
              size="sm"
              variant="outline"
              onClick={() => {
                merge.mutate(
                  { id: tag.id, target: mergeInto },
                  {
                    onSuccess: onDone,
                    onError: (e) => {
                      notifyError(e)
                    },
                  },
                )
              }}
            >
              Merge
            </Button>
          )}
        </p>
      )}
    </form>
  )
}

function MergeDialog({
  sources,
  all,
  onClose,
}: {
  sources: Tag[]
  all: Tag[]
  onClose: () => void
}) {
  const merge = useMergeTag()
  const ids = new Set(sources.map((s) => s.id))
  const options = all.filter((t) => !ids.has(t.id))
  const [target, setTarget] = useState<string>(options[0]?.id ?? '')
  const [busy, setBusy] = useState(false)
  const label = sources.map((s) => `#${s.name}`).join(', ')
  return (
    <Dialog
      open
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Merge {label}</DialogTitle>
          <DialogDescription>
            Every resource with {sources.length === 1 ? 'this tag' : 'these tags'} gets the tag you
            pick, and {sources.length === 1 ? 'it is' : 'they are'} removed.
          </DialogDescription>
        </DialogHeader>
        {options.length === 0 ? (
          <p className="text-sm text-muted-foreground">There is no other tag to merge into.</p>
        ) : (
          <Field>
            <FieldLabel htmlFor="merge-target">Merge into</FieldLabel>
            <Select value={target} onValueChange={setTarget}>
              <SelectTrigger id="merge-target" className="w-full">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                {options.map((t) => (
                  <SelectItem key={t.id} value={t.id}>
                    #{t.name}
                  </SelectItem>
                ))}
              </SelectContent>
            </Select>
          </Field>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <LoadingButton
            loading={busy}
            disabled={!target}
            onClick={() => {
              setBusy(true)
              void (async () => {
                for (const s of sources) await merge.mutateAsync({ id: s.id, target })
              })().then(
                () => {
                  notify(`Merged ${label} into #${all.find((t) => t.id === target)?.name ?? ''}`)
                  onClose()
                },
                (e: unknown) => {
                  setBusy(false)
                  notifyError(e)
                },
              )
            }}
          >
            Merge
          </LoadingButton>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
