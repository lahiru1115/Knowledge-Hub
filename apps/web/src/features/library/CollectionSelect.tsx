import { useQuery } from '@tanstack/react-query'
import { Inbox } from 'lucide-react'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { collectionsQuery } from './queries'

const INBOX = 'inbox'

interface Props {
  id?: string
  value: string | null | undefined
  onChange: (collectionId: string | null) => void
  /** IDs to leave out, for example the collection being deleted. */
  exclude?: string[]
  'aria-label'?: string
}

/** Collection picker with the Inbox as the "no collection" choice (FR-RES-06). */
export function CollectionSelect({
  id,
  value,
  onChange,
  exclude = [],
  'aria-label': ariaLabel,
}: Props) {
  const { data } = useQuery(collectionsQuery)
  return (
    <Select
      value={value ?? INBOX}
      onValueChange={(v) => {
        onChange(v === INBOX ? null : v)
      }}
    >
      <SelectTrigger id={id} className="w-full" aria-label={ariaLabel}>
        <SelectValue />
      </SelectTrigger>
      <SelectContent>
        <SelectItem value={INBOX}>
          <Inbox aria-hidden="true" /> Inbox
        </SelectItem>
        {data?.items
          .filter((c) => !exclude.includes(c.id))
          .map((c) => (
            <SelectItem key={c.id} value={c.id}>
              <span aria-hidden="true">{c.icon ?? '📁'}</span> {c.name}
            </SelectItem>
          ))}
      </SelectContent>
    </Select>
  )
}
