import { Download } from 'lucide-react'
import { buttonVariants } from '@/components/ui/button'
import { cn } from '@/lib/utils'

/** The files are plain downloads: the browser sends the session cookie and saves the response (FR-DAT-01). */
export function ExportLinks({ className }: { className?: string }) {
  return (
    <div className={cn('flex flex-wrap gap-2', className)}>
      <a
        href="/api/v1/export?format=json"
        download
        className={buttonVariants({ variant: 'outline' })}
      >
        <Download aria-hidden="true" /> Export JSON
      </a>
      <a
        href="/api/v1/export?format=csv"
        download
        className={buttonVariants({ variant: 'outline' })}
      >
        <Download aria-hidden="true" /> Export CSV
      </a>
    </div>
  )
}

export function DataSection() {
  return (
    <section aria-labelledby="data-title" className="max-w-xl space-y-4">
      <h2 id="data-title" className="text-xl font-semibold">
        Your data
      </h2>
      <p className="text-sm">
        Download everything you have saved: resources with their notes, summaries and tags, and your
        collections. Archived resources are included; deleted ones are not.
      </p>
      <ul className="list-disc space-y-1 pl-5 text-sm">
        <li>
          <strong>JSON</strong> keeps everything and is the best choice for a backup or to move to
          another tool.
        </li>
        <li>
          <strong>CSV</strong> opens in a spreadsheet, one row per resource.
        </li>
      </ul>
      <ExportLinks />
    </section>
  )
}
