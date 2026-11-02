import { useSuspenseQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'
import { FormAlert } from '@/components/FormAlert'
import { Field, FieldLabel } from '@/components/ui/field'
import { ToggleGroup, ToggleGroupItem } from '@/components/ui/toggle-group'
import { meQueryOptions } from '@/features/auth/session'
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
      <Field>
        <FieldLabel id="theme-label">Theme</FieldLabel>
        <ToggleGroup
          type="single"
          variant="outline"
          value={current}
          aria-labelledby="theme-label"
          onValueChange={(value) => {
            // Radix reports an empty value when the active item is pressed again; keep the current theme.
            if (value) void choose(value as ThemePreference)
          }}
        >
          {options.map((o) => (
            <ToggleGroupItem key={o.value} value={o.value}>
              {o.label}
            </ToggleGroupItem>
          ))}
        </ToggleGroup>
      </Field>
      {error && <FormAlert>{error}</FormAlert>}
    </section>
  )
}
