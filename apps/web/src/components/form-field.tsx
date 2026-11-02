import { Eye, EyeOff } from 'lucide-react'
import { forwardRef, useId, useState, type ComponentProps, type ReactNode } from 'react'
import { Field, FieldDescription, FieldError, FieldLabel } from '@/components/ui/field'
import { Input } from '@/components/ui/input'
import {
  InputGroup,
  InputGroupAddon,
  InputGroupButton,
  InputGroupInput,
} from '@/components/ui/input-group'

interface FormFieldProps extends Omit<ComponentProps<'input'>, 'ref'> {
  label: string
  error?: string
  hint?: ReactNode
}

/** Wires the ids shadcn's Field parts leave to the caller: label, hint and error are tied to the control. */
function useFieldIds(id: string | undefined, hint: ReactNode, error: string | undefined) {
  const auto = useId()
  const inputId = id ?? auto
  const hintId = `${inputId}-hint`
  const errorId = `${inputId}-error`
  const describedBy = [hint ? hintId : null, error ? errorId : null].filter(Boolean).join(' ')
  return { inputId, hintId, errorId, describedBy: describedBy || undefined }
}

export const FormField = forwardRef<HTMLInputElement, FormFieldProps>(function FormField(
  { label, error, hint, id, ...rest },
  ref,
) {
  const ids = useFieldIds(id, hint, error)
  return (
    <Field data-invalid={error ? true : undefined}>
      <FieldLabel htmlFor={ids.inputId}>{label}</FieldLabel>
      <Input
        {...rest}
        ref={ref}
        id={ids.inputId}
        aria-invalid={error ? true : undefined}
        aria-describedby={ids.describedBy}
      />
      {hint && <FieldDescription id={ids.hintId}>{hint}</FieldDescription>}
      {error && <FieldError id={ids.errorId}>{error}</FieldError>}
    </Field>
  )
})

/** A password input with a show/hide button (WCAG 3.3.8: paste and password managers keep working). */
export const PasswordField = forwardRef<HTMLInputElement, FormFieldProps>(function PasswordField(
  { label, error, hint, id, ...rest },
  ref,
) {
  const ids = useFieldIds(id, hint, error)
  const [visible, setVisible] = useState(false)
  const Icon = visible ? EyeOff : Eye
  return (
    <Field data-invalid={error ? true : undefined}>
      <FieldLabel htmlFor={ids.inputId}>{label}</FieldLabel>
      <InputGroup>
        <InputGroupInput
          {...rest}
          ref={ref}
          id={ids.inputId}
          type={visible ? 'text' : 'password'}
          aria-invalid={error ? true : undefined}
          aria-describedby={ids.describedBy}
        />
        <InputGroupAddon align="inline-end">
          <InputGroupButton
            size="icon-xs"
            aria-label={visible ? 'Hide password' : 'Show password'}
            aria-pressed={visible}
            onClick={() => {
              setVisible((v) => !v)
            }}
          >
            <Icon aria-hidden="true" />
          </InputGroupButton>
        </InputGroupAddon>
      </InputGroup>
      {hint && <FieldDescription id={ids.hintId}>{hint}</FieldDescription>}
      {error && <FieldError id={ids.errorId}>{error}</FieldError>}
    </Field>
  )
})
