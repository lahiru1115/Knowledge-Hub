import { zodResolver } from '@hookform/resolvers/zod'
import { Link, useSearch } from '@tanstack/react-router'
import { Check, CircleCheck } from 'lucide-react'
import { useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'
import { ApiProblem, api, unwrap } from '@/api/client'
import { messageFor } from '@/api/problems'
import { FormAlert } from '@/components/FormAlert'
import { Button } from '@/components/ui/button'
import { LoadingButton } from '@/components/loading-button'
import { PasswordField } from '@/components/form-field'
import { AuthLayout } from './AuthLayout'
import { applyProblem } from './formErrors'

const schema = z.object({
  new_password: z
    .string()
    .min(12, 'Use at least 12 characters.')
    .max(128, 'Use 128 characters or fewer.'),
})
type Values = z.infer<typeof schema>

export function ResetPasswordPage() {
  const { token } = useSearch({ from: '/reset-password' })
  const [done, setDone] = useState(false)
  const [linkProblem, setLinkProblem] = useState(token === undefined)
  const [formError, setFormError] = useState<string>()
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitting },
  } = useForm<Values>({ resolver: zodResolver(schema), defaultValues: { new_password: '' } })
  const longEnough = useWatch({ control, name: 'new_password' }).length >= 12

  const onSubmit = handleSubmit(async (values) => {
    setFormError(undefined)
    try {
      await unwrap(
        api.POST('/auth/password-reset/confirm', {
          body: { token: token ?? '', new_password: values.new_password },
        }),
      )
      setDone(true)
    } catch (e) {
      if (e instanceof ApiProblem && e.code === 'invalid_token') {
        setLinkProblem(true)
        return
      }
      setFormError(applyProblem(e, setError, ['new_password']))
    }
  })

  if (done) {
    return (
      <AuthLayout title="Password updated">
        <div role="status" className="space-y-3 text-sm">
          <CircleCheck className="size-6 text-success" aria-hidden="true" />
          <p>
            Your password has been changed and you&apos;ve been signed out everywhere. Sign in with
            the new one.
          </p>
        </div>
        <Button asChild className="w-full">
          <Link to="/login" search={{}}>
            Sign in
          </Link>
        </Button>
      </AuthLayout>
    )
  }

  if (linkProblem) {
    return (
      <AuthLayout title="This link can't be used">
        <p role="alert" className="text-sm">
          {messageFor('invalid_token')}
        </p>
        <Link
          to="/forgot-password"
          className="font-medium text-primary underline underline-offset-4"
        >
          Request a new link
        </Link>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title="Choose a new password">
      <form onSubmit={(e) => void onSubmit(e)} noValidate className="space-y-4">
        {formError && <FormAlert>{formError}</FormAlert>}
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
        <LoadingButton type="submit" loading={isSubmitting} className="w-full">
          Update password
        </LoadingButton>
      </form>
    </AuthLayout>
  )
}
