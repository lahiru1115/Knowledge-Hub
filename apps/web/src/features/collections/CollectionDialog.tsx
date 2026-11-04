import { useState } from 'react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
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
import { Field, FieldDescription, FieldError, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import { RadioGroup, RadioGroupItem } from '@/components/ui/radio-group'
import { Textarea } from '@/components/ui/textarea'
import { COLLECTION_COLORS, type Collection } from '@/features/library/types'
import { cn } from '@/lib/utils'
import { useCreateCollection, useUpdateCollection } from './collectionMutations'
import type { components } from '@/api/schema'

type CollectionColor = NonNullable<components['schemas']['CollectionCreate']['color']>

/** Tailwind classes for the collection colour swatches (the colours are named, not free-form: FR-COL-02). */
export const COLOR_CLASS: Record<string, string> = {
  slate: 'bg-slate-500',
  teal: 'bg-teal-500',
  blue: 'bg-blue-500',
  violet: 'bg-violet-500',
  rose: 'bg-rose-500',
  amber: 'bg-amber-500',
  green: 'bg-green-500',
  orange: 'bg-orange-500',
}

const EMOJI_CHOICES = ['📘', '📚', '🎓', '🧠', '💼', '🔬', '🎬', '🛠️', '⭐', '🌍']

/** Create or edit a collection: name, description, emoji and colour (Doc 03 §5.6). */
export function CollectionDialog({
  collection,
  onClose,
  onSaved,
}: {
  collection?: Collection
  onClose: () => void
  onSaved?: (c: Collection) => void
}) {
  const create = useCreateCollection()
  const update = useUpdateCollection()
  const [name, setName] = useState(collection?.name ?? '')
  const [description, setDescription] = useState(collection?.description ?? '')
  const [icon, setIcon] = useState(collection?.icon ?? '')
  const [color, setColor] = useState(collection?.color ?? '')
  const [errors, setErrors] = useState<Record<string, string>>({})
  const [formError, setFormError] = useState<string>()
  const busy = create.isPending || update.isPending

  async function submit() {
    const next: Record<string, string> = {}
    if (!name.trim()) next.name = 'Enter a name.'
    setErrors(next)
    if (Object.keys(next).length > 0) return
    setFormError(undefined)
    const body = {
      name: name.trim(),
      description: description.trim() || null,
      icon: icon.trim() || null,
      color: (color || null) as CollectionColor | null,
    }
    try {
      const saved = collection
        ? await update.mutateAsync({ id: collection.id, body })
        : await create.mutateAsync(body)
      onSaved?.(saved)
      onClose()
    } catch (e) {
      if (e instanceof ApiProblem && e.code === 'collection_name_taken') {
        setErrors({ name: `You already have a collection called '${name.trim()}'.` })
      } else if (e instanceof ApiProblem && e.code === 'validation_failed') {
        setErrors(Object.fromEntries(Object.entries(e.errors).map(([k, v]) => [k, v[0] ?? ''])))
      } else {
        setFormError(e instanceof ApiProblem ? messageFor(e.code, e) : messageFor('internal_error'))
      }
    }
  }

  return (
    <Dialog
      open
      onOpenChange={(o) => {
        if (!o) onClose()
      }}
    >
      <DialogContent className="sm:max-w-md">
        <DialogHeader>
          <DialogTitle>{collection ? 'Edit collection' : 'New collection'}</DialogTitle>
          <DialogDescription>
            Group related resources, for example one collection per course.
          </DialogDescription>
        </DialogHeader>
        <form
          className="space-y-4"
          noValidate
          onSubmit={(e) => {
            e.preventDefault()
            void submit()
          }}
        >
          {formError && (
            <p role="alert" className="text-sm text-destructive">
              {formError}
            </p>
          )}
          <Field data-invalid={errors.name ? true : undefined}>
            <FieldLabel htmlFor="col-name">Name</FieldLabel>
            <Input
              id="col-name"
              value={name}
              maxLength={100}
              aria-invalid={errors.name ? true : undefined}
              aria-describedby={errors.name ? 'col-name-error' : undefined}
              onChange={(e) => {
                setName(e.target.value)
              }}
            />
            {errors.name && <FieldError id="col-name-error">{errors.name}</FieldError>}
          </Field>
          <Field data-invalid={errors.description ? true : undefined}>
            <FieldLabel htmlFor="col-desc">Description (optional)</FieldLabel>
            <Textarea
              id="col-desc"
              rows={2}
              maxLength={500}
              value={description}
              onChange={(e) => {
                setDescription(e.target.value)
              }}
            />
            {errors.description && <FieldError>{errors.description}</FieldError>}
          </Field>
          <Field data-invalid={errors.icon ? true : undefined}>
            <FieldLabel htmlFor="col-icon">Emoji (optional)</FieldLabel>
            <Input
              id="col-icon"
              value={icon}
              maxLength={8}
              className="w-24"
              aria-describedby="col-icon-hint"
              aria-invalid={errors.icon ? true : undefined}
              onChange={(e) => {
                setIcon(e.target.value)
              }}
            />
            <FieldDescription id="col-icon-hint">One emoji, or pick one:</FieldDescription>
            <div className="flex flex-wrap gap-1">
              {EMOJI_CHOICES.map((e) => (
                <Button
                  key={e}
                  type="button"
                  variant={icon === e ? 'secondary' : 'ghost'}
                  size="icon-sm"
                  aria-label={`Use ${e}`}
                  aria-pressed={icon === e}
                  onClick={() => {
                    setIcon(icon === e ? '' : e)
                  }}
                >
                  {e}
                </Button>
              ))}
            </div>
            {errors.icon && <FieldError>{errors.icon}</FieldError>}
          </Field>
          <fieldset className="space-y-2">
            <legend className="text-sm font-medium">Colour (optional)</legend>
            <RadioGroup
              value={color}
              onValueChange={setColor}
              className="flex flex-wrap gap-2"
              aria-label="Colour"
            >
              {COLLECTION_COLORS.map((c) => (
                <RadioGroupItem
                  key={c}
                  value={c}
                  aria-label={c}
                  className={cn(
                    'size-6 border-2 border-transparent data-[state=checked]:border-foreground',
                    COLOR_CLASS[c],
                  )}
                />
              ))}
            </RadioGroup>
          </fieldset>
          <DialogFooter>
            <Button type="button" variant="outline" onClick={onClose}>
              Cancel
            </Button>
            <LoadingButton type="submit" loading={busy}>
              {collection ? 'Save changes' : 'Create collection'}
            </LoadingButton>
          </DialogFooter>
        </form>
      </DialogContent>
    </Dialog>
  )
}
