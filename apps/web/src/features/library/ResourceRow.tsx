import {
  Archive,
  ArchiveRestore,
  ExternalLink,
  FolderInput,
  Globe,
  Link2,
  MoreHorizontal,
  Star,
  Trash2,
  FileText,
  Film,
  GraduationCap,
  GitBranch,
  Newspaper,
  type LucideIcon,
} from 'lucide-react'
import { toast } from 'sonner'
import { Badge } from '@/components/ui/badge'
import { Button } from '@/components/ui/button'
import { Checkbox } from '@/components/ui/checkbox'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { cn } from '@/lib/utils'
import { siteOf, timeAgo } from '@/lib/format'
import { FetchStatus } from './FetchStatus'
import { typeLabel, type Resource, type ResourceType } from './types'

const ICONS: Record<ResourceType, LucideIcon> = {
  article: Newspaper,
  video: Film,
  pdf: FileText,
  course: GraduationCap,
  repo: GitBranch,
  website: Globe,
  other: Link2,
}

export function TypeIcon({ type, className }: { type: ResourceType; className?: string }) {
  const Icon = ICONS[type]
  return <Icon className={className} aria-hidden="true" />
}

export interface RowActions {
  open: (r: Resource) => void
  toggleFavourite: (r: Resource) => void
  toggleArchived: (r: Resource) => void
  move: (r: Resource) => void
  remove: (r: Resource) => void
  filterTag: (tag: string) => void
}

interface Props {
  resource: Resource
  collectionLabel?: string
  selected: boolean
  active: boolean
  onSelect: (r: Resource, selected: boolean) => void
  actions: RowActions
}

/** One resource in the list (Doc 03 §5.2): select box, title and details, tags, quick favourite and a menu. */
export function ResourceRow({
  resource: r,
  collectionLabel,
  selected,
  active,
  onSelect,
  actions,
}: Props) {
  const title = r.title ?? r.url
  return (
    <li
      data-active={active || undefined}
      data-resource-id={r.id}
      className={cn(
        'flex gap-3 border-b border-border px-3 py-3 last:border-b-0 data-[active]:bg-accent/60',
        selected && 'bg-accent',
      )}
    >
      <Checkbox
        className="mt-1"
        aria-label={`Select ${title}`}
        checked={selected}
        onCheckedChange={(c) => {
          onSelect(r, c === true)
        }}
      />
      <div className="min-w-0 flex-1">
        <button
          type="button"
          className="block w-full truncate text-left font-medium hover:underline focus-visible:underline"
          title={title}
          onClick={() => {
            actions.open(r)
          }}
        >
          <TypeIcon type={r.type} className="mr-2 inline size-4 text-muted-foreground" />
          {title}
        </button>
        <p className="truncate text-sm text-muted-foreground">
          {siteOf(r.url)} · {typeLabel(r.type)}
          {r.is_archived && ' · Archived'}
        </p>
        <FetchStatus resource={r} className="mt-1" />
        {r.description && <p className="mt-1 line-clamp-2 text-sm">{r.description}</p>}
        <div className="mt-2 flex flex-wrap items-center gap-x-3 gap-y-1">
          {r.tags.length > 0 && (
            <ul className="flex flex-wrap gap-1" aria-label="Tags">
              {r.tags.map((t) => (
                <li key={t}>
                  <Badge asChild variant="secondary" className="font-normal">
                    <button
                      type="button"
                      aria-label={`Filter by tag ${t}`}
                      onClick={() => {
                        actions.filterTag(t)
                      }}
                    >
                      #{t}
                    </button>
                  </Badge>
                </li>
              ))}
            </ul>
          )}
          <span className="text-xs text-muted-foreground">
            {collectionLabel ?? 'Inbox'} · {timeAgo(r.created_at)}
          </span>
        </div>
      </div>
      <div className="flex shrink-0 items-start gap-1">
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label={
            r.is_favourite ? `Remove ${title} from favourites` : `Add ${title} to favourites`
          }
          aria-pressed={r.is_favourite}
          onClick={() => {
            actions.toggleFavourite(r)
          }}
        >
          <Star className={cn(r.is_favourite && 'fill-current text-warning')} aria-hidden="true" />
        </Button>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon-sm" aria-label={`Actions for ${title}`}>
              <MoreHorizontal aria-hidden="true" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem
              onSelect={() => {
                actions.open(r)
              }}
            >
              <ExternalLink /> Open details
            </DropdownMenuItem>
            <DropdownMenuItem
              onSelect={() => {
                actions.move(r)
              }}
            >
              <FolderInput /> Move to…
            </DropdownMenuItem>
            <DropdownMenuItem
              onSelect={() => {
                actions.toggleArchived(r)
              }}
            >
              {r.is_archived ? <ArchiveRestore /> : <Archive />}{' '}
              {r.is_archived ? 'Unarchive' : 'Archive'}
            </DropdownMenuItem>
            <DropdownMenuItem
              onSelect={() => {
                void navigator.clipboard.writeText(r.url).then(() => toast('Link copied'))
              }}
            >
              <Link2 /> Copy link
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuItem
              variant="destructive"
              onSelect={() => {
                actions.remove(r)
              }}
            >
              <Trash2 /> Delete
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </li>
  )
}
