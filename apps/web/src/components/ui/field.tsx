import { Eye, EyeOff } from 'lucide-react'
import { forwardRef, useId, useState, type InputHTMLAttributes, type ReactNode } from 'react'
import { cn } from '@/lib/cn'

interface FieldProps extends InputHTMLAttributes<HTMLInputElement> {
  label: string
  error?: string
  hint?: ReactNode
}

const inputClass =
  'h-10 w-full rounded-sm border border-border-control bg-bg px-3 text-base text-text ' +
  'placeholder:text-text-muted aria-[invalid=true]:border-danger'

/** A labelled input with its hint and error wired up for screen readers (WCAG 1.3.1, 3.3.1). */
export const Field = forwardRef<HTMLInputElement, FieldProps>(function Field(
  { label, error, hint, className, id, ...rest },
  ref,
) {
  const auto = useId()
  const inputId = id ?? auto
  const hintId = `${inputId}-hint`
  const errorId = `${inputId}-error`
  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ')
  return (
    <div className="space-y-1">
      <label htmlFor={inputId} className="block text-sm font-medium">
        {label}
      </label>
      <input
        {...rest}
        ref={ref}
        id={inputId}
        aria-invalid={error ? true : undefined}
        aria-describedby={describedBy || undefined}
        className={cn(inputClass, className)}
      />
      {hint && (
        <p id={hintId} className="text-sm text-text-muted">
          {hint}
        </p>
      )}
      {error && (
        <p id={errorId} className="text-sm text-danger">
          {error}
        </p>
      )}
    </div>
  )
})

export const PasswordField = forwardRef<HTMLInputElement, FieldProps>(
  function PasswordField(props, ref) {
    const [visible, setVisible] = useState(false)
    const Icon = visible ? EyeOff : Eye
    return (
      <div className="relative">
        <Field {...props} ref={ref} type={visible ? 'text' : 'password'} className="pr-11" />
        <button
          type="button"
          onClick={() => {
            setVisible((v) => !v)
          }}
          aria-label={visible ? 'Hide password' : 'Show password'}
          aria-pressed={visible}
          className="absolute right-1 top-[26px] grid size-8 place-items-center rounded-sm text-text-muted hover:bg-bg-subtle"
        >
          <Icon className="size-4" aria-hidden="true" />
        </button>
      </div>
    )
  },
)
