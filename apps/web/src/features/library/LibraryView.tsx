import { useQuery } from '@tanstack/react-query'
import { Plus } from 'lucide-react'
import { useMemo, useState, type ReactNode } from 'react'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import { plural } from '@/lib/format'
import { useKeyShortcuts } from '@/lib/shortcuts'
import { usePageTitle } from '@/lib/usePageTitle'
import { BulkBar } from './BulkBar'
import { BulkTagsDialog, ConfirmBulkDelete, MoveDialog } from './Dialogs'
import { FilterBar, type SetSearch } from './FilterBar'
import {
  activeFilterCount,
  DEFAULT_PAGE_SIZE,
  toApiQuery,
  type LibrarySearch,
} from './librarySearch'
import { useBulk, useDeleteResource, useUpdateResource } from './mutations'
import { Pagination } from './Pagination'
import { useQuickAdd } from './QuickAdd'
import { collectionsQuery, resourcesQuery } from './queries'
import { ResourceDrawer } from './ResourceDrawer'
import { ResourceRow, type RowActions } from './ResourceRow'
import type { Resource } from './types'

interface Props {
  search: LibrarySearch
  setSearch: SetSearch
  /** Set on a collection page: the list is limited to this collection. */
  scopeCollectionId?: string
  title: string
  /** Shown under the title, for a collection page's description and Edit button. */
  header?: ReactNode
}

/** The Library screen (SCR-LIB) and, with a scope, a collection page (SCR-COLS). */
export function LibraryView({ search, setSearch, scopeCollectionId, title, header }: Props) {
  usePageTitle(title)
  const quickAdd = useQuickAdd()
  const apiQuery = toApiQuery(search, scopeCollectionId)
  const list = useQuery(resourcesQuery(apiQuery))
  const { data: collections } = useQuery(collectionsQuery)
  const update = useUpdateResource()
  const remove = useDeleteResource()
  const bulk = useBulk()

  const [selected, setSelected] = useState<Set<string>>(new Set())
  const [activeId, setActiveId] = useState<string>()
  const [moving, setMoving] = useState<Resource[] | null>(null)
  const [tagging, setTagging] = useState<'add' | 'remove' | null>(null)
  const [confirmDelete, setConfirmDelete] = useState(false)

  const items = list.data?.items ?? []
  const total = list.data?.total ?? 0
  const collectionName = useMemo(
    () => new Map((collections?.items ?? []).map((c) => [c.id, `${c.icon ?? '📁'} ${c.name}`])),
    [collections],
  )
  const filtered = activeFilterCount(search) > 0 || Boolean(search.q)

  // A new page or filter starts a fresh selection.
  const queryKey = JSON.stringify(apiQuery)
  const [lastKey, setLastKey] = useState(queryKey)
  if (queryKey !== lastKey) {
    setLastKey(queryKey)
    setSelected(new Set())
  }

  const actions: RowActions = {
    open: (r) => {
      setSearch({ r: r.id })
    },
    toggleFavourite: (r) => {
      update.mutate({ id: r.id, body: { is_favourite: !r.is_favourite } })
    },
    toggleArchived: (r) => {
      update.mutate({ id: r.id, body: { is_archived: !r.is_archived } })
    },
    move: (r) => {
      setMoving([r])
    },
    remove: (r) => {
      remove.mutate(r)
    },
    filterTag: (tag) => {
      setSearch({ tag: [...new Set([...(search.tag ?? []), tag])], page: undefined })
    },
  }

  const activeIndex = items.findIndex((r) => r.id === activeId)
  const move = (delta: number) => {
    if (items.length === 0) return
    const next = Math.min(
      items.length - 1,
      Math.max(0, (activeIndex < 0 ? (delta > 0 ? -1 : items.length) : activeIndex) + delta),
    )
    const id = items[next]?.id
    setActiveId(id)
    document.querySelector(`[data-resource-id="${id ?? ''}"]`)?.scrollIntoView({ block: 'nearest' })
  }
  const current = items[activeIndex]
  useKeyShortcuts(
    {
      j: () => {
        move(1)
      },
      k: () => {
        move(-1)
      },
      Enter: () => {
        if (current) actions.open(current)
      },
      o: () => {
        if (current) actions.open(current)
      },
      x: () => {
        if (current) toggleSelected(current, !selected.has(current.id))
      },
      f: () => {
        if (current) actions.toggleFavourite(current)
      },
      e: () => {
        if (current) actions.toggleArchived(current)
      },
      Delete: () => {
        if (current) actions.remove(current)
      },
      Backspace: () => {
        if (current) actions.remove(current)
      },
    },
    !search.r,
  )

  function toggleSelected(r: Resource, on: boolean) {
    setSelected((s) => {
      const next = new Set(s)
      if (on) next.add(r.id)
      else next.delete(r.id)
      return next
    })
  }

  const ids = [...selected]
  const selectedResources = items.filter((r) => selected.has(r.id))

  return (
    <div className="space-y-4">
      <header className="space-y-1">
        <div className="flex flex-wrap items-baseline justify-between gap-2">
          <h1 className="text-2xl font-semibold">{title}</h1>
          <p className="text-sm text-muted-foreground" aria-live="polite">
            {list.data ? plural(total, 'resource') : ''}
          </p>
        </div>
        {header}
      </header>

      {selected.size > 0 ? (
        <BulkBar
          count={selected.size}
          onMove={() => {
            setMoving(selectedResources)
          }}
          onAddTags={() => {
            setTagging('add')
          }}
          onRemoveTags={() => {
            setTagging('remove')
          }}
          onDelete={() => {
            if (selected.size > 10) setConfirmDelete(true)
            else
              bulk.mutate(
                { action: 'delete', ids },
                {
                  onSuccess: () => {
                    setSelected(new Set())
                  },
                },
              )
          }}
          onCancel={() => {
            setSelected(new Set())
          }}
        />
      ) : (
        <FilterBar search={search} setSearch={setSearch} scoped={scopeCollectionId !== undefined} />
      )}

      {list.isPending ? (
        <ul aria-busy="true" aria-label="Loading resources" className="divide-y rounded-md border">
          {Array.from({ length: 6 }, (_, i) => (
            <li key={i} className="space-y-2 p-4">
              <Skeleton className="h-4 w-2/3" />
              <Skeleton className="h-3 w-1/3" />
            </li>
          ))}
        </ul>
      ) : list.isError ? (
        <div
          role="alert"
          className="space-y-3 rounded-md border border-destructive p-6 text-center"
        >
          <p className="font-medium">Couldn&apos;t load your library.</p>
          <Button variant="outline" onClick={() => void list.refetch()}>
            Try again
          </Button>
        </div>
      ) : items.length === 0 ? (
        filtered ? (
          <div className="space-y-3 rounded-md border p-8 text-center">
            <p className="font-medium">
              {search.q
                ? `Nothing matches '${search.q}' with these filters.`
                : 'Nothing matches these filters.'}
            </p>
            <Button
              variant="outline"
              onClick={() => {
                setSearch({
                  q: undefined,
                  type: undefined,
                  tag: undefined,
                  collection: scopeCollectionId ? undefined : undefined,
                  favourite: undefined,
                  archived: undefined,
                  page: undefined,
                })
              }}
            >
              Clear filters
            </Button>
          </div>
        ) : (
          <div className="space-y-3 rounded-md border p-8 text-center">
            <p className="text-lg font-medium">
              {scopeCollectionId ? 'Nothing here yet.' : 'Your library is empty.'}
            </p>
            <p className="text-muted-foreground">
              {scopeCollectionId
                ? "Use 'Move to…' on a resource, or add a new one here."
                : 'Save links to articles, videos, courses and repos.'}
            </p>
            <Button
              onClick={() => {
                quickAdd.open({ collectionId: scopeCollectionId })
              }}
            >
              <Plus aria-hidden="true" />{' '}
              {scopeCollectionId ? 'Add a resource' : 'Add your first resource'}
            </Button>
            <p className="text-xs text-muted-foreground">Or press N.</p>
          </div>
        )
      ) : (
        <>
          <ul aria-label="Resources" className="rounded-md border">
            {items.map((r) => (
              <ResourceRow
                key={r.id}
                resource={r}
                collectionLabel={r.collection_id ? collectionName.get(r.collection_id) : undefined}
                selected={selected.has(r.id)}
                active={r.id === activeId}
                onSelect={toggleSelected}
                actions={actions}
              />
            ))}
          </ul>
          <Pagination
            page={search.page ?? 1}
            pageSize={search.size ?? DEFAULT_PAGE_SIZE}
            total={total}
            onPage={(page) => {
              setSearch({ page: page === 1 ? undefined : page })
            }}
            onPageSize={(size) => {
              setSearch({ size: size === DEFAULT_PAGE_SIZE ? undefined : size, page: undefined })
            }}
          />
        </>
      )}

      {moving && (
        <MoveDialog
          count={moving.length}
          initial={moving.length === 1 ? moving[0]?.collection_id : null}
          onMove={(collectionId) =>
            bulk
              .mutateAsync({
                action: 'move',
                ids: moving.map((r) => r.id),
                collection_id: collectionId,
              })
              .then(() => {
                setSelected(new Set())
              })
          }
          onClose={() => {
            setMoving(null)
          }}
        />
      )}
      {tagging && (
        <BulkTagsDialog
          mode={tagging}
          count={selected.size}
          onApply={(tags) =>
            bulk
              .mutateAsync({ action: tagging === 'add' ? 'add_tags' : 'remove_tags', ids, tags })
              .then(() => {
                setSelected(new Set())
              })
          }
          onClose={() => {
            setTagging(null)
          }}
        />
      )}
      {confirmDelete && (
        <ConfirmBulkDelete
          count={selected.size}
          onConfirm={() => {
            bulk.mutate(
              { action: 'delete', ids },
              {
                onSuccess: () => {
                  setSelected(new Set())
                },
              },
            )
          }}
          onClose={() => {
            setConfirmDelete(false)
          }}
        />
      )}

      {search.r && (
        <ResourceDrawer
          id={search.r}
          siblings={items.map((r) => r.id)}
          onNavigate={(id) => {
            setSearch({ r: id }, { replace: true })
          }}
          onClose={() => {
            setSearch({ r: undefined })
          }}
        />
      )}
    </div>
  )
}
