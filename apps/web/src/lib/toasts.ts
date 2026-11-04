import { toast } from 'sonner'
import { ApiProblem } from '@/api/client'
import { messageFor } from '@/api/problems'

export const UNDO_MS = 10_000
export const TOAST_MS = 6_000

export function notify(
  message: string,
  opts?: { action?: { label: string; onClick: () => void }; undo?: boolean },
) {
  toast(message, {
    duration: opts?.undo || opts?.action ? UNDO_MS : TOAST_MS,
    action: opts?.action,
  })
}

/** A toast with Undo (10 seconds, Doc 03 §6.2). */
export function notifyUndo(message: string, onUndo: () => void) {
  toast(message, { duration: UNDO_MS, action: { label: 'Undo', onClick: onUndo } })
}

/** Failure toast with the user-facing text for the API error (never raw server text, IR-03). */
export function notifyError(
  error: unknown,
  fallback = 'Something went wrong on our side. Try again in a moment.',
) {
  toast.error(error instanceof ApiProblem ? messageFor(error.code, error) : fallback, {
    duration: TOAST_MS,
  })
}
