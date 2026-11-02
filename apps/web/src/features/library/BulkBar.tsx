import { X } from 'lucide-react'
import { Button } from '@/components/ui/button'

interface Props {
  count: number
  onMove: () => void
  onAddTags: () => void
  onRemoveTags: () => void
  onDelete: () => void
  onCancel: () => void
}

/** Replaces the filter row while resources are selected (Doc 03 §5.2). */
export function BulkBar({ count, onMove, onAddTags, onRemoveTags, onDelete, onCancel }: Props) {
  return (
    <div
      role="toolbar"
      aria-label="Bulk actions"
      className="flex flex-wrap items-center gap-2 rounded-md border border-border bg-muted px-3 py-2"
    >
      <span className="text-sm font-medium" aria-live="polite">
        {count} selected
      </span>
      <Button size="sm" variant="outline" onClick={onMove}>
        Move to…
      </Button>
      <Button size="sm" variant="outline" onClick={onAddTags}>
        Add tags
      </Button>
      <Button size="sm" variant="outline" onClick={onRemoveTags}>
        Remove tags
      </Button>
      <Button
        size="sm"
        variant="outline"
        className="text-destructive hover:text-destructive"
        onClick={onDelete}
      >
        Delete
      </Button>
      <Button size="sm" variant="ghost" className="ml-auto" onClick={onCancel}>
        Cancel <X aria-hidden="true" />
      </Button>
    </div>
  )
}
