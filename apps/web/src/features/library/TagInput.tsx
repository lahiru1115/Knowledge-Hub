import { useQuery } from '@tanstack/react-query'
import { Plus, X } from 'lucide-react'
import { useId, useState } from 'react'
import { Badge } from '@/components/ui/badge'
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from '@/components/ui/command'
import { FieldLabel } from '@/components/ui/field'
import { tagsQuery } from './queries'

const TAG_PATTERN = /^[\p{L}\p{N} \-_.#+]{1,50}$/u

/** The same rules as the server (FR-TAG-01): trimmed, lowercased, 1 to 50 letters, digits, spaces and - _ . # +. */
export function normalizeTag(input: string): string | undefined {
  const name = input.trim().replace(/ {2,}/g, ' ').toLowerCase()
  return TAG_PATTERN.test(name) ? name : undefined
}

interface Props {
  label?: string
  value: string[]
  onChange: (tags: string[]) => void
  max?: number
}

/**
 * Tag chips plus a combobox: existing tags match as you type, and "Create '#foo'" adds a new one.
 * Chips are buttons labelled "Remove tag foo" (Doc 03 §9.2).
 */
export function TagInput({ label = 'Tags', value, onChange, max = 20 }: Props) {
  const id = useId()
  const [text, setText] = useState('')
  const [open, setOpen] = useState(false)
  const { data } = useQuery(tagsQuery)

  const typed = normalizeTag(text)
  const existing = (data?.items ?? [])
    .map((t) => t.name)
    .filter((n) => !value.includes(n) && n.includes(text.trim().toLowerCase()))
    .slice(0, 8)
  const canCreate = typed !== undefined && !value.includes(typed) && !existing.includes(typed)
  const atLimit = value.length >= max
  const showList = open && (existing.length > 0 || canCreate)

  function add(tag: string) {
    if (atLimit || value.includes(tag)) return
    onChange([...value, tag])
    setText('')
  }

  return (
    <div className="space-y-2">
      <FieldLabel htmlFor={id}>{label}</FieldLabel>
      {value.length > 0 && (
        <ul className="flex flex-wrap gap-1.5" aria-label="Selected tags">
          {value.map((tag) => (
            <li key={tag}>
              <Badge variant="secondary" className="gap-1 pr-1 text-sm font-normal">
                #{tag}
                <button
                  type="button"
                  aria-label={`Remove tag ${tag}`}
                  className="grid size-5 place-items-center rounded-sm hover:bg-background/60"
                  onClick={() => {
                    onChange(value.filter((t) => t !== tag))
                  }}
                >
                  <X className="size-3" aria-hidden="true" />
                </button>
              </Badge>
            </li>
          ))}
        </ul>
      )}
      <Command
        shouldFilter={false}
        className="overflow-visible rounded-md border border-input bg-transparent"
      >
        <CommandInput
          id={id}
          data-escape-local={showList ? '' : undefined}
          aria-expanded={showList}
          value={text}
          disabled={atLimit}
          placeholder={atLimit ? `Up to ${max.toString()} tags` : 'Add a tag…'}
          onValueChange={(v) => {
            setText(v)
            setOpen(true)
          }}
          onFocus={() => {
            setOpen(true)
          }}
          onBlur={() => {
            // Let a click on an option register before the list closes.
            setTimeout(() => {
              setOpen(false)
            }, 120)
          }}
          onKeyDown={(e) => {
            if (e.key === 'Enter' && typed && existing.length === 0) {
              e.preventDefault()
              e.stopPropagation()
              add(typed)
            } else if (e.key === 'Backspace' && text === '' && value.length > 0) {
              onChange(value.slice(0, -1))
            } else if (e.key === 'Escape' && showList) {
              e.stopPropagation()
              setOpen(false)
            }
          }}
        />
        {/* Always in the page, so the input's aria-controls points at a real element; hidden until there is something to pick. */}
        {
          <CommandList className={showList ? 'max-h-44' : 'hidden'}>
            <CommandEmpty>No matching tags.</CommandEmpty>
            <CommandGroup>
              {existing.map((name) => (
                <CommandItem
                  key={name}
                  value={name}
                  onSelect={() => {
                    add(name)
                  }}
                >
                  #{name}
                </CommandItem>
              ))}
              {canCreate && (
                <CommandItem
                  value={`create-${typed}`}
                  onSelect={() => {
                    add(typed)
                  }}
                >
                  <Plus aria-hidden="true" /> Create &lsquo;#{typed}&rsquo;
                </CommandItem>
              )}
            </CommandGroup>
          </CommandList>
        }
      </Command>
      {text.trim() !== '' && typed === undefined && (
        <p role="alert" className="text-sm text-destructive">
          Tags use letters, digits, spaces and - _ . # + (up to 50 characters).
        </p>
      )}
    </div>
  )
}
