import { useQuery } from '@tanstack/react-query'
import { useMemo, useState } from 'react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
import { Button } from '@/components/ui/button'
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from '@/components/ui/select'
import { Skeleton } from '@/components/ui/skeleton'
import { usePageTitle } from '@/lib/usePageTitle'
import { adminUsageQuery, adminUsersQuery } from './queries'

const DAYS = 30
const money = new Intl.NumberFormat('en', {
  style: 'currency',
  currency: 'USD',
  minimumFractionDigits: 2,
  maximumFractionDigits: 4,
})
const number = new Intl.NumberFormat('en')
const dayFormat = new Intl.DateTimeFormat('en', { day: 'numeric', month: 'short', timeZone: 'UTC' })

interface Row {
  date: string
  operation: string
  calls: number
  tokens_in: number
  tokens_out: number
  cost_usd: number
}

interface Day {
  date: string
  calls: number
  tokens_in: number
  tokens_out: number
  cost_usd: number
}

/** The last 30 UTC days, oldest first, zero where nothing happened. Rows are summed over operations. */
export function perDay(rows: readonly Row[], now: Date = new Date()): Day[] {
  const days: Day[] = Array.from({ length: DAYS }, (_, i) => {
    const d = new Date(
      Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate() - (DAYS - 1 - i)),
    )
    return {
      date: d.toISOString().slice(0, 10),
      calls: 0,
      tokens_in: 0,
      tokens_out: 0,
      cost_usd: 0,
    }
  })
  const byDate = new Map(days.map((d) => [d.date, d]))
  for (const r of rows) {
    const d = byDate.get(r.date)
    if (!d) continue
    d.calls += r.calls
    d.tokens_in += r.tokens_in
    d.tokens_out += r.tokens_out
    d.cost_usd += r.cost_usd
  }
  return days
}

function perOperation(rows: readonly Row[]): Row[] {
  const map = new Map<string, Row>()
  for (const r of rows) {
    const t = map.get(r.operation) ?? { ...r, calls: 0, tokens_in: 0, tokens_out: 0, cost_usd: 0 }
    t.calls += r.calls
    t.tokens_in += r.tokens_in
    t.tokens_out += r.tokens_out
    t.cost_usd += r.cost_usd
    map.set(r.operation, t)
  }
  return [...map.values()].sort((a, b) => b.cost_usd - a.cost_usd)
}

const sum = (days: readonly Day[], key: 'calls' | 'cost_usd') =>
  days.reduce((n, d) => n + d[key], 0)

function CostChart({ days }: { days: readonly Day[] }) {
  const max = Math.max(...days.map((d) => d.cost_usd))
  return (
    <div className="space-y-2">
      <div
        role="img"
        aria-label={`Cost for each of the last ${DAYS.toString()} days, ${money.format(sum(days, 'cost_usd'))} in all. The table below has the numbers.`}
        className="flex h-24 items-end gap-1"
      >
        {days.map((d) => (
          <span
            key={d.date}
            className="flex-1 rounded-t-sm bg-primary/80"
            style={{
              height: `${(max > 0 ? Math.max(d.cost_usd > 0 ? 6 : 2, (d.cost_usd / max) * 100) : 2).toString()}%`,
            }}
            title={`${dayFormat.format(new Date(d.date))}: ${money.format(d.cost_usd)}`}
          />
        ))}
      </div>
      <p className="text-xs text-muted-foreground">Days are UTC. Oldest on the left.</p>
    </div>
  )
}

function DayTable({ days, caption }: { days: readonly Day[]; caption: string }) {
  return (
    <details className="text-sm">
      <summary className="cursor-pointer text-muted-foreground">Show as a table</summary>
      <div className="mt-2 overflow-x-auto">
        <table className="w-full text-left">
          <caption className="sr-only">{caption}</caption>
          <thead>
            <tr>
              <th scope="col" className="py-1 font-medium">
                Day
              </th>
              <th scope="col" className="py-1 text-right font-medium">
                Calls
              </th>
              <th scope="col" className="py-1 text-right font-medium">
                Tokens in
              </th>
              <th scope="col" className="py-1 text-right font-medium">
                Tokens out
              </th>
              <th scope="col" className="py-1 text-right font-medium">
                Cost
              </th>
            </tr>
          </thead>
          <tbody>
            {[...days].reverse().map((d) => (
              <tr key={d.date} className="border-t border-border">
                <th scope="row" className="py-1 font-normal">
                  {d.date}
                </th>
                <td className="py-1 text-right tabular-nums">{number.format(d.calls)}</td>
                <td className="py-1 text-right tabular-nums">{number.format(d.tokens_in)}</td>
                <td className="py-1 text-right tabular-nums">{number.format(d.tokens_out)}</td>
                <td className="py-1 text-right tabular-nums">{money.format(d.cost_usd)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </details>
  )
}

function Problem({ error, retry }: { error: unknown; retry: () => void }) {
  const unavailable = error instanceof ApiProblem && error.code === 'ai_unavailable'
  return (
    <div role="alert" className="space-y-2">
      <p>
        {unavailable
          ? "The AI service isn't answering, so its usage can't be shown right now."
          : error instanceof ApiProblem
            ? messageFor(error.code, error)
            : 'Could not load AI usage.'}
      </p>
      <Button variant="outline" onClick={retry}>
        Try again
      </Button>
    </div>
  )
}

function OneUser() {
  const [userId, setUserId] = useState<string>()
  const users = useQuery(adminUsersQuery('', 1, 100))
  const usage = useQuery({ ...adminUsageQuery(userId), enabled: userId !== undefined })
  const days = useMemo(() => perDay(usage.data?.items ?? []), [usage.data])
  const chosen = users.data?.items.find((u) => u.id === userId)
  return (
    <section aria-labelledby="one-user" className="space-y-3">
      <h2 id="one-user" className="text-lg font-semibold">
        One user
      </h2>
      <Select value={userId} onValueChange={setUserId}>
        <SelectTrigger aria-label="Choose a user" className="w-full max-w-sm">
          <SelectValue placeholder="Choose a user" />
        </SelectTrigger>
        <SelectContent>
          {(users.data?.items ?? []).map((u) => (
            <SelectItem key={u.id} value={u.id}>
              {u.email}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      {userId !== undefined &&
        (usage.isError ? (
          <Problem
            error={usage.error}
            retry={() => {
              void usage.refetch()
            }}
          />
        ) : usage.isPending ? (
          <Skeleton className="h-24 w-full" />
        ) : (
          <div className="space-y-2">
            <p className="text-sm">
              {chosen?.email}: {number.format(sum(days, 'calls'))} calls,{' '}
              {money.format(sum(days, 'cost_usd'))} in the last {DAYS.toString()} days.
            </p>
            <CostChart days={days} />
            <DayTable days={days} caption={`AI usage per day for ${chosen?.email ?? 'the user'}`} />
          </div>
        ))}
    </section>
  )
}

/** SCR-ADMIN AI usage: cost and calls per day for the last 30 days, overall and for one user (FR-ADM-03). */
export function AiUsagePage() {
  usePageTitle('Admin · AI usage')
  const { data, isPending, isError, error, refetch } = useQuery(adminUsageQuery())
  const days = useMemo(() => perDay(data?.items ?? []), [data])
  const operations = useMemo(() => perOperation(data?.items ?? []), [data])

  return (
    <div className="space-y-8">
      <section aria-labelledby="all-users" className="space-y-3">
        <h2 id="all-users" className="text-lg font-semibold">
          Everyone, last {DAYS.toString()} days
        </h2>
        {isError ? (
          <Problem
            error={error}
            retry={() => {
              void refetch()
            }}
          />
        ) : isPending ? (
          <Skeleton className="h-32 w-full" />
        ) : (
          <>
            <p className="text-sm">
              {number.format(sum(days, 'calls'))} calls, {money.format(sum(days, 'cost_usd'))} in
              all.
            </p>
            <CostChart days={days} />
            <DayTable days={days} caption="AI usage per day for all users" />
            {operations.length > 0 && (
              <div className="overflow-x-auto">
                <table className="w-full text-left text-sm">
                  <caption className="sr-only">AI usage by operation</caption>
                  <thead>
                    <tr>
                      <th scope="col" className="py-1 font-medium">
                        Operation
                      </th>
                      <th scope="col" className="py-1 text-right font-medium">
                        Calls
                      </th>
                      <th scope="col" className="py-1 text-right font-medium">
                        Cost
                      </th>
                    </tr>
                  </thead>
                  <tbody>
                    {operations.map((o) => (
                      <tr key={o.operation} className="border-t border-border">
                        <th scope="row" className="py-1 font-normal">
                          {o.operation.replace('_', ' ')}
                        </th>
                        <td className="py-1 text-right tabular-nums">{number.format(o.calls)}</td>
                        <td className="py-1 text-right tabular-nums">{money.format(o.cost_usd)}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </>
        )}
      </section>
      <OneUser />
    </div>
  )
}
