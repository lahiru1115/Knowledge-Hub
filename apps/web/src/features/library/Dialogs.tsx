import { useState } from 'react'
import { LoadingButton } from '@/components/loading-button'
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from '@/components/ui/alert-dialog'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Field, FieldLabel } from '@/components/ui/field'
import { CollectionSelect } from './CollectionSelect'
import { TagInput } from './TagInput'

/** "Move to…" for one resource or a selection: the non-drag way to move things (WCAG 2.5.7). */
export function MoveDialog({
  count,
  initial,
  onMove,
  onClose,
}: {
  count: number
  initial?: string | null
  onMove: (collectionId: string | null) => Promise<unknown>
  onClose: () => void
}) {
  const [target, setTarget] = useState<string | null>(initial ?? null)
  const [busy, setBusy] = useState(false)
  return (
    <Dialog
      open
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>
            {count === 1 ? 'Move resource' : `Move ${count.toString()} resources`}
          </DialogTitle>
          <DialogDescription>
            Choose where to put {count === 1 ? 'it' : 'them'}. Tags and notes stay as they are.
          </DialogDescription>
        </DialogHeader>
        <Field>
          <FieldLabel htmlFor="move-target">Collection</FieldLabel>
          <CollectionSelect id="move-target" value={target} onChange={setTarget} />
        </Field>
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <LoadingButton
            loading={busy}
            onClick={() => {
              setBusy(true)
              void onMove(target).then(onClose, () => {
                setBusy(false)
              })
            }}
          >
            {count === 1 ? 'Move resource' : `Move ${count.toString()} resources`}
          </LoadingButton>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** Add or remove tags on a selection (FR-RES-12). */
export function BulkTagsDialog({
  mode,
  count,
  onApply,
  onClose,
}: {
  mode: 'add' | 'remove'
  count: number
  onApply: (tags: string[]) => Promise<unknown>
  onClose: () => void
}) {
  const [tags, setTags] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const verb = mode === 'add' ? 'Add' : 'Remove'
  return (
    <Dialog
      open
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>
            {verb} tags on {count === 1 ? '1 resource' : `${count.toString()} resources`}
          </DialogTitle>
          <DialogDescription>
            {mode === 'add'
              ? 'These tags are added to each selected resource.'
              : 'These tags are removed from each selected resource.'}
          </DialogDescription>
        </DialogHeader>
        <TagInput value={tags} onChange={setTags} />
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <LoadingButton
            loading={busy}
            disabled={tags.length === 0}
            onClick={() => {
              setBusy(true)
              void onApply(tags).then(onClose, () => {
                setBusy(false)
              })
            }}
          >
            {verb}{' '}
            {tags.length > 0
              ? `${tags.length.toString()} tag${tags.length === 1 ? '' : 's'}`
              : 'tags'}
          </LoadingButton>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}

/** Bulk delete of more than 10 items asks first; everything else is undo instead of confirm (Doc 03 §6.2). */
export function ConfirmBulkDelete({
  count,
  onConfirm,
  onClose,
}: {
  count: number
  onConfirm: () => void
  onClose: () => void
}) {
  return (
    <AlertDialog
      open
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete {count.toString()} resources?</AlertDialogTitle>
          <AlertDialogDescription>
            They disappear from your library now. You can undo for a few seconds, and restore them
            for 7 days.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction onClick={onConfirm}>
            Delete {count.toString()} resources
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  )
}
