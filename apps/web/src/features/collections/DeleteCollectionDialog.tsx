import { useState } from 'react'
import { LoadingButton } from '@/components/loading-button'
import { Button } from '@/components/ui/button'
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from '@/components/ui/dialog'
import { Label } from '@/components/ui/label'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { CollectionSelect } from '@/features/library/CollectionSelect'
import { notify, notifyError } from '@/lib/toasts'
import { plural } from '@/lib/format'
import { useDeleteCollection } from './collectionMutations'
import type { Collection } from '@/features/library/types'

/** The one confirm dialog in the app for collections: the user chooses what happens to the resources (FR-COL-05). */
export function DeleteCollectionDialog({
  collection,
  onClose,
  onDeleted,
}: {
  collection: Collection
  onClose: () => void
  onDeleted?: () => void
}) {
  const del = useDeleteCollection()
  const [choice, setChoice] = useState<'move' | 'delete'>('move')
  const [target, setTarget] = useState<string | null>(null)
  const count = collection.resource_count

  return (
    <Dialog
      open
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>Delete &ldquo;{collection.name}&rdquo;?</DialogTitle>
          <DialogDescription>
            {count === 0
              ? 'It has no resources.'
              : `It contains ${plural(count, 'resource')}. What should happen to them?`}
          </DialogDescription>
        </DialogHeader>
        {count > 0 && (
          <RadioGroup
            value={choice}
            onValueChange={(v) => {
              setChoice(v as 'move' | 'delete')
            }}
            aria-label="What happens to the resources"
          >
            <div className="flex items-center gap-2">
              <RadioGroupItem id="del-move" value="move" />
              <Label htmlFor="del-move">Move them to</Label>
              <div className="min-w-0 flex-1">
                <CollectionSelect
                  aria-label="Move resources to"
                  value={target}
                  onChange={setTarget}
                  exclude={[collection.id]}
                />
              </div>
            </div>
            <div className="flex items-center gap-2">
              <RadioGroupItem id="del-delete" value="delete" />
              <Label htmlFor="del-delete">Delete them too</Label>
            </div>
          </RadioGroup>
        )}
        <DialogFooter>
          <Button variant="outline" onClick={onClose}>
            Cancel
          </Button>
          <LoadingButton
            variant="destructive"
            loading={del.isPending}
            onClick={() => {
              del.mutate(
                {
                  id: collection.id,
                  resources: count === 0 ? 'move' : choice,
                  target: choice === 'move' ? target : null,
                },
                {
                  onSuccess: () => {
                    notify(`Deleted '${collection.name}'`)
                    onDeleted?.()
                    onClose()
                  },
                  onError: (e) => {
                    notifyError(e)
                  },
                },
              )
            }}
          >
            Delete
          </LoadingButton>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  )
}
