import { zodResolver } from '@hookform/resolvers/zod'
import { Link } from '@tanstack/react-router'
import { MailCheck } from 'lucide-react'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { api, unwrap } from '@/api/client'
import { FormAlert } from '@/components/FormAlert'
import { LoadingButton } from '@/components/loading-button'
import { FormField } from '@/components/form-field'
import { AuthLayout } from './AuthLayout'
import { applyProblem } from './formErrors'

const schema = z.object({ email: z.email('Enter a valid email address.') })
type Values = z.infer<typeof schema>

export function ForgotPasswordPage() {
  const [sentTo, setSentTo] = useState<string>()
  const [formError, setFormError] = useState<string>()
  const {
    register,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting },
  } = useForm<Values>({ resolver: zodResolver(schema) })

  const onSubmit = handleSubmit(async (values) => {
    setFormError(undefined)
    try {
      await unwrap(api.POST('/auth/password-reset', { body: values }))
      // The same message is shown whether or not an account exists (FR-AUTH-06).
      setSentTo(values.email)
    } catch (e) {
      setFormError(applyProblem(e, setError, ['email']))
    }
  })

  if (sentTo) {
    return (
      <AuthLayout title="Check your email">
        <div role="status" className="space-y-3 text-sm">
          <MailCheck className="size-6 text-primary" aria-hidden="true" />
          <p>
            If there&apos;s an account for <strong>{sentTo}</strong>, we&apos;ve sent a link to
            reset the password. It works once and expires in 30 minutes.
          </p>
        </div>
        <p className="text-center text-sm">
          <Link
            to="/login"
            search={{}}
            className="font-medium text-primary underline underline-offset-4"
          >
            Back to sign in
          </Link>
        </p>
      </AuthLayout>
    )
  }

  return (
    <AuthLayout title="Reset your password">
      <form onSubmit={(e) => void onSubmit(e)} noValidate className="space-y-4">
        <p className="text-sm text-muted-foreground">
          Enter the email you signed up with and we&apos;ll send you a link to choose a new
          password.
        </p>
        {formError && <FormAlert>{formError}</FormAlert>}
        <FormField
          label="Email"
          type="email"
          autoComplete="email"
          error={errors.email?.message}
          {...register('email')}
        />
        <LoadingButton type="submit" loading={isSubmitting} className="w-full">
          Send reset link
        </LoadingButton>
      </form>
      <p className="text-center text-sm">
        <Link
          to="/login"
          search={{}}
          className="font-medium text-primary underline underline-offset-4"
        >
          Back to sign in
        </Link>
      </p>
    </AuthLayout>
  )
}
