import { ChevronLeft, ChevronRight } from 'lucide-react'
import { Button } from '@/components/ui/button'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'

interface Props {
  page: number
  pageSize: number
  total: number
  onPage: (page: number) => void
  onPageSize: (size: number) => void
}

/** Numbered offset pages with a page-size choice (FR-SRCH-04, Doc 03 §5.2). */
export function Pagination({ page, pageSize, total, onPage, onPageSize }: Props) {
  const pages = Math.max(1, Math.ceil(total / pageSize))
  return (
    <nav aria-label="Pagination" className="flex flex-wrap items-center justify-between gap-3 py-3">
      <div className="flex items-center gap-2">
        <Button
          variant="outline"
          size="sm"
          disabled={page <= 1}
          onClick={() => {
            onPage(page - 1)
          }}
        >
          <ChevronLeft aria-hidden="true" /> Prev
        </Button>
        <span className="text-sm" aria-live="polite">
          Page {page} of {pages}
        </span>
        <Button
          variant="outline"
          size="sm"
          disabled={page >= pages}
          onClick={() => {
            onPage(page + 1)
          }}
        >
          Next <ChevronRight aria-hidden="true" />
        </Button>
      </div>
      <Select
        value={String(pageSize)}
        onValueChange={(v) => {
          onPageSize(Number(v))
        }}
      >
        <SelectTrigger size="sm" aria-label="Resources per page" className="w-36">
          <SelectValue />
        </SelectTrigger>
        <SelectContent>
          {[20, 50, 100].map((n) => (
            <SelectItem key={n} value={String(n)}>
              {n} per page
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
    </nav>
  )
}
