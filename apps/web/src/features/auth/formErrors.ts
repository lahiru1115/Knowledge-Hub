import type { FieldValues, Path, UseFormSetError } from 'react-hook-form'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'

/**
 * Puts a failed request's problem where the user can see it: field messages under their fields, anything
 * else in the form-level alert. Returns the form-level message (or undefined if fields took it all).
 */
export function applyProblem<T extends FieldValues>(
  error: unknown,
  setError: UseFormSetError<T>,
  fields: readonly Path<T>[],
): string | undefined {
  if (!(error instanceof ApiProblem)) return messageFor('internal_error')

  let placed = false
  for (const [name, messages] of Object.entries(error.errors)) {
    const field = fields.find((f) => f === name)
    if (field && messages[0]) {
      setError(field, { type: 'server', message: messages[0] })
      placed = true
    }
  }
  if (error.code === 'validation_failed' && placed) return undefined
  return messageFor(error.code, error)
}
