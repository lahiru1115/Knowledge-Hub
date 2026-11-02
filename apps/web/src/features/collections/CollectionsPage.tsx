import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import {
  ArrowDown,
  ArrowUp,
  Inbox,
  MoreHorizontal,
  Pencil,
  Pin,
  PinOff,
  Plus,
  Trash2,
} from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Skeleton } from '@/components/ui/skeleton'
import { collectionsQuery } from '@/features/library/queries'
import type { Collection } from '@/features/library/types'
import { plural } from '@/lib/format'
import { usePageTitle } from '@/lib/usePageTitle'
import { useReorderCollections, useUpdateCollection } from './collectionMutations'
import { CollectionDialog } from './CollectionDialog'
import { DeleteCollectionDialog } from './DeleteCollectionDialog'

/** Collections list (SCR-COLS). Reorder with Move up and Move down: the keyboard alternative to dragging (WCAG 2.5.7). */
export function CollectionsPage() {
  usePageTitle('Collections')
  const { data, isPending, isError, refetch } = useQuery(collectionsQuery)
  const reorder = useReorderCollections()
  const update = useUpdateCollection()
  const [dialog, setDialog] = useState<{ edit?: Collection } | null>(null)
  const [deleting, setDeleting] = useState<Collection | null>(null)
  const items = data?.items ?? []

  function moveBy(c: Collection, delta: number) {
    const i = items.findIndex((x) => x.id === c.id)
    const j = i + delta
    const other = items[j]
    if (!other || other.is_pinned !== c.is_pinned) return // pinned ones stay above the others
    const ids = items.map((x) => x.id)
    ;[ids[i], ids[j]] = [ids[j] as string, ids[i] as string]
    reorder.mutate(ids)
  }

  return (
    <div className="space-y-4">
      <header className="flex flex-wrap items-center justify-between gap-2">
        <h1 className="text-2xl font-semibold">Collections</h1>
        <Button
          onClick={() => {
            setDialog({})
          }}
        >
          <Plus aria-hidden="true" /> New collection
        </Button>
      </header>

      {isPending ? (
        <ul aria-busy="true" className="divide-y rounded-md border">
          {[0, 1, 2].map((i) => (
            <li key={i} className="p-4">
              <Skeleton className="h-4 w-1/2" />
            </li>
          ))}
        </ul>
      ) : isError ? (
        <div
          role="alert"
          className="space-y-3 rounded-md border border-destructive p-6 text-center"
        >
          <p className="font-medium">Couldn&apos;t load your collections.</p>
          <Button variant="outline" onClick={() => void refetch()}>
            Try again
          </Button>
        </div>
      ) : (
        <>
          {items.length === 0 && (
            <p className="rounded-md border p-6 text-center text-muted-foreground">
              Group related resources, for example one collection per course.
            </p>
          )}
          <ul aria-label="Collections" className="rounded-md border">
            {items.map((c, index) => (
              <li
                key={c.id}
                className="flex items-center gap-3 border-b border-border px-3 py-3 last:border-b-0"
              >
                <span className="w-5 text-center" aria-hidden="true">
                  {c.is_pinned ? <Pin className="size-4" /> : null}
                </span>
                <span className="text-xl" aria-hidden="true">
                  {c.icon ?? '📁'}
                </span>
                <div className="min-w-0 flex-1">
                  <Link
                    to="/collections/$id"
                    params={{ id: c.id }}
                    className="font-medium hover:underline"
                  >
                    {c.name}
                    {c.is_pinned && <span className="sr-only"> (pinned)</span>}
                  </Link>
                  {c.description && (
                    <p className="truncate text-sm text-muted-foreground">{c.description}</p>
                  )}
                </div>
                <span className="text-sm text-muted-foreground">
                  {plural(c.resource_count, 'resource')}
                </span>
                <DropdownMenu>
                  <DropdownMenuTrigger asChild>
                    <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${c.name}`}>
                      <MoreHorizontal aria-hidden="true" />
                    </Button>
                  </DropdownMenuTrigger>
                  <DropdownMenuContent align="end">
                    <DropdownMenuItem
                      onSelect={() => {
                        setDialog({ edit: c })
                      }}
                    >
                      <Pencil /> Edit
                    </DropdownMenuItem>
                    <DropdownMenuItem
                      onSelect={() => {
                        update.mutate({ id: c.id, body: { is_pinned: !c.is_pinned } })
                      }}
                    >
                      {c.is_pinned ? <PinOff /> : <Pin />} {c.is_pinned ? 'Unpin' : 'Pin'}
                    </DropdownMenuItem>
                    <DropdownMenuItem
                      disabled={index === 0 || items[index - 1]?.is_pinned !== c.is_pinned}
                      onSelect={() => {
                        moveBy(c, -1)
                      }}
                    >
                      <ArrowUp /> Move up
                    </DropdownMenuItem>
                    <DropdownMenuItem
                      disabled={
                        index === items.length - 1 || items[index + 1]?.is_pinned !== c.is_pinned
                      }
                      onSelect={() => {
                        moveBy(c, 1)
                      }}
                    >
                      <ArrowDown /> Move down
                    </DropdownMenuItem>
                    <DropdownMenuSeparator />
                    <DropdownMenuItem
                      variant="destructive"
                      onSelect={() => {
                        setDeleting(c)
                      }}
                    >
                      <Trash2 /> Delete…
                    </DropdownMenuItem>
                  </DropdownMenuContent>
                </DropdownMenu>
              </li>
            ))}
            <li className="flex items-center gap-3 px-3 py-3">
              <span className="w-5" aria-hidden="true" />
              <Inbox className="size-5" aria-hidden="true" />
              <div className="min-w-0 flex-1">
                <Link
                  to="/library"
                  search={{ collection: 'inbox' }}
                  className="font-medium hover:underline"
                >
                  Inbox
                </Link>
                <p className="text-sm text-muted-foreground">Not in a collection yet</p>
              </div>
              <span className="text-sm text-muted-foreground">
                {plural(data.inbox_count, 'resource')}
              </span>
              <span className="size-8" aria-hidden="true" />
            </li>
          </ul>
        </>
      )}

      {dialog && (
        <CollectionDialog
          collection={dialog.edit}
          onClose={() => {
            setDialog(null)
          }}
        />
      )}
      {deleting && (
        <DeleteCollectionDialog
          collection={deleting}
          onClose={() => {
            setDeleting(null)
          }}
        />
      )}
    </div>
  )
}
