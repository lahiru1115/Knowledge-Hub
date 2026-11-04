import { useQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { Pencil, Trash2 } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import { Skeleton } from '@/components/ui/skeleton'
import type { SetSearch } from '@/features/library/FilterBar'
import type { LibrarySearch } from '@/features/library/librarySearch'
import { LibraryView } from '@/features/library/LibraryView'
import { collectionsQuery } from '@/features/library/queries'
import { usePageTitle } from '@/lib/usePageTitle'
import { CollectionDialog } from './CollectionDialog'
import { DeleteCollectionDialog } from './DeleteCollectionDialog'

/** A collection page (SCR-COLS): the Library view for one collection, under a header with its emoji, name and description. */
export function CollectionPage({
  id,
  search,
  setSearch,
}: {
  id: string
  search: LibrarySearch
  setSearch: SetSearch
}) {
  const { data, isPending } = useQuery(collectionsQuery)
  const navigate = useNavigate()
  const [editing, setEditing] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const collection = data?.items.find((c) => c.id === id)
  usePageTitle(collection?.name ?? 'Collection')

  if (isPending) return <Skeleton className="h-8 w-1/3" aria-busy="true" />
  if (!collection) {
    return (
      <div role="alert" className="space-y-3 rounded-md border p-8 text-center">
        <p className="font-medium">This collection doesn&apos;t exist or was deleted.</p>
        <Button asChild variant="outline">
          <Link to="/collections">Back to collections</Link>
        </Button>
      </div>
    )
  }

  return (
    <>
      <LibraryView
        title={`${collection.icon ? `${collection.icon} ` : ''}${collection.name}`}
        search={search}
        setSearch={setSearch}
        scopeCollectionId={id}
        header={
          <div className="flex flex-wrap items-center gap-2">
            {collection.description && (
              <p className="text-sm text-muted-foreground">{collection.description}</p>
            )}
            <Button
              size="sm"
              variant="outline"
              onClick={() => {
                setEditing(true)
              }}
            >
              <Pencil aria-hidden="true" /> Edit
            </Button>
            <Button
              size="sm"
              variant="outline"
              onClick={() => {
                setDeleting(true)
              }}
            >
              <Trash2 aria-hidden="true" /> Delete…
            </Button>
          </div>
        }
      />
      {editing && (
        <CollectionDialog
          collection={collection}
          onClose={() => {
            setEditing(false)
          }}
        />
      )}
      {deleting && (
        <DeleteCollectionDialog
          collection={collection}
          onClose={() => {
            setDeleting(false)
          }}
          onDeleted={() => void navigate({ to: '/collections' })}
        />
      )}
    </>
  )
}
