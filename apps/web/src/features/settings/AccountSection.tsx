import { zodResolver } from '@hookform/resolvers/zod'
import { Link } from '@tanstack/react-router'
import { Check } from 'lucide-react'
import { useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'
import { ApiProblem, api, unwrap } from '@/api/client'
import { FormAlert } from '@/components/FormAlert'
import { Button } from '@/components/ui/button'
import { LoadingButton } from '@/components/loading-button'
import { PasswordField } from '@/components/form-field'
import { applyProblem } from '@/features/auth/formErrors'
import { useSignOut } from '@/features/auth/session'

const schema = z.object({
  current_password: z.string().min(1, 'Enter your current password.'),
  new_password: z
    .string()
    .min(12, 'Use at least 12 characters.')
    .max(128, 'Use 128 characters or fewer.'),
})
type Values = z.infer<typeof schema>

export function AccountSection() {
  const signOut = useSignOut()
  const [saved, setSaved] = useState(false)
  const [formError, setFormError] = useState<string>()
  const {
    register,
    handleSubmit,
    setError,
    reset,
    control,
    formState: { errors, isSubmitting },
  } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { current_password: '', new_password: '' },
  })
  const longEnough = useWatch({ control, name: 'new_password' }).length >= 12

  const onSubmit = handleSubmit(async (values) => {
    setSaved(false)
    setFormError(undefined)
    try {
      await unwrap(api.PUT('/me/password', { body: values }))
      reset()
      setSaved(true)
    } catch (e) {
      if (e instanceof ApiProblem && e.code === 'invalid_credentials') {
        setError('current_password', {
          type: 'server',
          message: "That password isn't right. Try again.",
        })
        return
      }
      setFormError(applyProblem(e, setError, ['current_password', 'new_password']))
    }
  })

  return (
    <div className="max-w-md space-y-10">
      <section aria-labelledby="password-title" className="space-y-4">
        <h2 id="password-title" className="text-xl font-semibold">
          Change password
        </h2>
        <form onSubmit={(e) => void onSubmit(e)} noValidate className="space-y-4">
          {formError && <FormAlert>{formError}</FormAlert>}
          <PasswordField
            label="Current password"
            autoComplete="current-password"
            error={errors.current_password?.message}
            {...register('current_password')}
          />
          <PasswordField
            label="New password"
            autoComplete="new-password"
            error={errors.new_password?.message}
            hint={
              <span className={longEnough ? 'text-success' : undefined}>
                {longEnough && <Check className="mr-1 inline size-4" aria-hidden="true" />}
                At least 12 characters
              </span>
            }
            {...register('new_password')}
          />
          <LoadingButton type="submit" loading={isSubmitting}>
            Change password
          </LoadingButton>
          <p role="status" className="text-sm text-success">
            {saved ? 'Password changed. You were signed out on your other devices.' : ''}
          </p>
        </form>
      </section>

      <section aria-labelledby="session-title" className="space-y-3">
        <h2 id="session-title" className="text-xl font-semibold">
          Sign out
        </h2>
        <LoadingButton
          variant="outline"
          loading={signOut.isPending}
          onClick={() => {
            signOut.mutate()
          }}
        >
          Sign out
        </LoadingButton>
      </section>

      <section
        aria-labelledby="danger-title"
        className="space-y-3 rounded-lg border border-destructive p-4"
      >
        <h2 id="danger-title" className="text-xl font-semibold text-destructive">
          Danger zone
        </h2>
        <p className="text-sm text-muted-foreground">
          Delete your account and everything you saved. This can&apos;t be undone.
        </p>
        <Button
          asChild
          variant="outline"
          className="border-destructive text-destructive hover:text-destructive"
        >
          <Link to="/settings/account/delete">Delete account…</Link>
        </Button>
      </section>
    </div>
  )
}
