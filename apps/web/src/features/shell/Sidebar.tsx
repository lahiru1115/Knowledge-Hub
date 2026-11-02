import { useQuery } from '@tanstack/react-query'
import { Link } from '@tanstack/react-router'
import { FolderOpen, Hash, Home, Inbox, Library, Plus, Settings } from 'lucide-react'
import { useState } from 'react'
import { CollectionDialog } from '@/features/collections/CollectionDialog'
import { collectionsQuery, tagsQuery } from '@/features/library/queries'
import { cn } from '@/lib/utils'

const item =
  'flex items-center gap-2 rounded-md px-2 py-1.5 text-sm hover:bg-accent hover:text-accent-foreground'
const active = {
  className: 'bg-accent font-medium text-accent-foreground',
  'aria-current': 'page' as const,
}

function Count({ n }: { n: number }) {
  return <span className="ml-auto text-xs text-muted-foreground">{n}</span>
}

/**
 * Desktop navigation (Doc 03 §4.1): Home and Library, collections with counts (pinned first), the most used
 * tags, and Settings. Collapses to an icon rail; the labels are then hidden but stay in the accessible name.
 */
export function Sidebar({ collapsed }: { collapsed: boolean }) {
  const { data: collections } = useQuery(collectionsQuery)
  const { data: tags } = useQuery(tagsQuery)
  const [creating, setCreating] = useState(false)
  const topTags = [...(tags?.items ?? [])]
    .sort((a, b) => b.resource_count - a.resource_count)
    .slice(0, 8)
  const label = (text: string) => <span className={cn(collapsed && 'sr-only')}>{text}</span>

  return (
    <nav
      aria-label="Main"
      className={cn(
        'hidden shrink-0 flex-col gap-4 overflow-y-auto border-r border-border p-3 md:flex',
        collapsed ? 'w-16' : 'w-64',
      )}
    >
      <div className="space-y-1">
        <Link to="/" className={item} activeProps={active} activeOptions={{ exact: true }}>
          <Home className="size-4" aria-hidden="true" /> {label('Home')}
        </Link>
        <Link to="/library" className={item} activeProps={active}>
          <Library className="size-4" aria-hidden="true" /> {label('Library')}
        </Link>
      </div>

      <div className="space-y-1">
        <div className="flex items-center justify-between px-2">
          <Link
            to="/collections"
            className={cn(
              'text-xs font-semibold tracking-wide text-muted-foreground uppercase hover:underline',
              collapsed && 'sr-only',
            )}
          >
            Collections
          </Link>
          <button
            type="button"
            aria-label="New collection"
            className="grid size-6 place-items-center rounded-md text-muted-foreground hover:bg-accent"
            onClick={() => {
              setCreating(true)
            }}
          >
            <Plus className="size-4" aria-hidden="true" />
          </button>
        </div>
        {collections?.items.map((c) => (
          <Link
            key={c.id}
            to="/collections/$id"
            params={{ id: c.id }}
            className={item}
            activeProps={active}
            title={c.name}
          >
            <span aria-hidden="true">{c.icon ?? <FolderOpen className="size-4" />}</span>
            <span className={cn('truncate', collapsed && 'sr-only')}>{c.name}</span>
            {c.is_pinned && <span className="sr-only">(pinned)</span>}
            {!collapsed && <Count n={c.resource_count} />}
          </Link>
        ))}
        <Link to="/library" search={{ collection: 'inbox' }} className={item} activeProps={active}>
          <Inbox className="size-4" aria-hidden="true" /> {label('Inbox')}
          {!collapsed && <Count n={collections?.inbox_count ?? 0} />}
        </Link>
      </div>

      {!collapsed && (
        <div className="space-y-1">
          <h2 className="px-2 text-xs font-semibold tracking-wide text-muted-foreground uppercase">
            Tags
          </h2>
          {topTags.map((t) => (
            <Link key={t.id} to="/library" search={{ tag: [t.name] }} className={item}>
              <Hash className="size-4" aria-hidden="true" />{' '}
              <span className="truncate">{t.name}</span>
              <Count n={t.resource_count} />
            </Link>
          ))}
          <Link to="/tags" className={cn(item, 'text-muted-foreground')}>
            Manage tags →
          </Link>
        </div>
      )}

      <div className="mt-auto">
        <Link to="/settings/profile" className={item} activeProps={active}>
          <Settings className="size-4" aria-hidden="true" /> {label('Settings')}
        </Link>
      </div>
      {creating && (
        <CollectionDialog
          onClose={() => {
            setCreating(false)
          }}
        />
      )}
    </nav>
  )
}
