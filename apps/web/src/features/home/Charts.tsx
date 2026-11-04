import { Link } from '@tanstack/react-router'
import { typeLabel, type ResourceType } from '@/features/library/types'
import type { Dashboard } from './queries'

/** Horizontal bars with the number written next to each, so nothing depends on colour (Doc 03 §5.4). */
export function TypeBars({ byType }: { byType: Dashboard['by_type'] }) {
  const rows = (Object.entries(byType) as [ResourceType, number][])
    .filter(([, n]) => n > 0)
    .sort((a, b) => b[1] - a[1])
  const max = Math.max(1, ...rows.map(([, n]) => n))
  if (rows.length === 0) return <p className="text-sm text-muted-foreground">Nothing saved yet.</p>
  return (
    <ul aria-label="Resources by type" className="space-y-2">
      {rows.map(([type, n]) => (
        <li key={type}>
          <Link
            to="/library"
            search={{ type: [type] }}
            className="grid grid-cols-[5rem_1fr_2.5rem] items-center gap-2 text-sm hover:underline"
          >
            <span>{typeLabel(type)}</span>
            <span className="h-3 rounded-sm bg-muted" aria-hidden="true">
              <span
                className="block h-3 rounded-sm bg-primary"
                style={{ width: `${((n / max) * 100).toString()}%` }}
              />
            </span>
            <span className="text-right tabular-nums">{n.toString()}</span>
          </Link>
        </li>
      ))}
    </ul>
  )
}

const weekFormat = new Intl.DateTimeFormat('en', {
  day: 'numeric',
  month: 'short',
  timeZone: 'UTC',
})

/** A small column chart of resources added per week, with the same numbers as a table for anyone who wants them. */
export function ActivityChart({ weeks }: { weeks: Dashboard['weekly_activity'] }) {
  const max = Math.max(1, ...weeks.map((w) => w.count))
  const total = weeks.reduce((n, w) => n + w.count, 0)
  return (
    <div className="space-y-2">
      <div
        role="img"
        aria-label={`Resources added in each of the last ${weeks.length.toString()} weeks, ${total.toString()} in all. The table below has the numbers.`}
        className="flex h-16 items-end gap-1"
      >
        {weeks.map((w) => (
          <span
            key={w.week_start}
            className="flex-1 rounded-t-sm bg-primary/80"
            style={{
              height: `${Math.max(w.count > 0 ? 8 : 2, (w.count / max) * 100).toString()}%`,
            }}
            title={`Week of ${weekFormat.format(new Date(w.week_start))}: ${w.count.toString()}`}
          />
        ))}
      </div>
      <details className="text-sm">
        <summary className="cursor-pointer text-muted-foreground">Show as a table</summary>
        <table className="mt-2 w-full text-left">
          <caption className="sr-only">Resources added per week</caption>
          <thead>
            <tr>
              <th scope="col" className="font-medium">
                Week starting
              </th>
              <th scope="col" className="text-right font-medium">
                Added
              </th>
            </tr>
          </thead>
          <tbody>
            {weeks.map((w) => (
              <tr key={w.week_start}>
                <th scope="row" className="font-normal">
                  {weekFormat.format(new Date(w.week_start))}
                </th>
                <td className="text-right tabular-nums">{w.count.toString()}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </details>
    </div>
  )
}
