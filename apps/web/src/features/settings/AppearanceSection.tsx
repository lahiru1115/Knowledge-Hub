import { useSuspenseQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
import { meQueryOptions } from '@/features/auth/session'
import { cn } from '@/lib/cn'
import { applyTheme, type ThemePreference } from '@/lib/theme'
import { useUpdateMe } from './useUpdateMe'

const options: { value: ThemePreference; label: string }[] = [
  { value: 'system', label: 'System' },
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
]

export function AppearanceSection() {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const update = useUpdateMe()
  const [error, setError] = useState<string>()
  const current = me.preferences.theme ?? 'system'

  async function choose(theme: ThemePreference) {
    if (theme === current) return
    setError(undefined)
    applyTheme(theme) // immediate; rolled back below if saving fails
    try {
      await update.mutateAsync({ preferences: { theme } })
    } catch (e) {
      applyTheme(current)
      setError(e instanceof ApiProblem ? messageFor(e.code, e) : messageFor('internal_error'))
    }
  }

  return (
    <section aria-labelledby="appearance-title" className="max-w-md space-y-4">
      <h2 id="appearance-title" className="text-xl font-semibold">
        Appearance
      </h2>
      <fieldset className="space-y-2">
        <legend className="text-sm font-medium">Theme</legend>
        <div className="inline-flex rounded-md border border-border-control p-0.5">
          {options.map((o) => (
            <label
              key={o.value}
              className={cn(
                'cursor-pointer rounded-sm px-4 py-1.5 text-sm has-[:focus-visible]:outline-2 has-[:focus-visible]:outline-offset-2 has-[:focus-visible]:outline-accent',
                current === o.value ? 'bg-accent text-accent-fg' : 'hover:bg-bg-subtle',
              )}
            >
              <input
                type="radio"
                name="theme"
                value={o.value}
                checked={current === o.value}
                onChange={() => void choose(o.value)}
                className="sr-only"
              />
              {o.label}
            </label>
          ))}
        </div>
      </fieldset>
      {error && (
        <p role="alert" className="text-sm text-danger">
          {error}
        </p>
      )}
    </section>
  )
}
