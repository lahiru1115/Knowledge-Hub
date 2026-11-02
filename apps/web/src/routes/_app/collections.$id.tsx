import { createFileRoute } from '@tanstack/react-router'
import { CollectionPage } from '@/features/collections/CollectionPage'
import { librarySearchSchema } from '@/features/library/librarySearch'

export const Route = createFileRoute('/_app/collections/$id')({
  validateSearch: librarySearchSchema,
  component: CollectionRoute,
})

function CollectionRoute() {
  const { id } = Route.useParams()
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <CollectionPage
      id={id}
      search={search}
      setSearch={(next, opts) =>
        void navigate({ search: (prev) => ({ ...prev, ...next }), replace: opts?.replace })
      }
    />
  )
}
