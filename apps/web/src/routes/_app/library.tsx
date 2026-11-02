import { createFileRoute } from '@tanstack/react-router'
import { LibraryView } from '@/features/library/LibraryView'
import { librarySearchSchema } from '@/features/library/librarySearch'

export const Route = createFileRoute('/_app/library')({
  validateSearch: librarySearchSchema,
  component: LibraryRoute,
})

function LibraryRoute() {
  const search = Route.useSearch()
  const navigate = Route.useNavigate()
  return (
    <LibraryView
      title="Library"
      search={search}
      setSearch={(next, opts) =>
        void navigate({ search: (prev) => ({ ...prev, ...next }), replace: opts?.replace })
      }
    />
  )
}
