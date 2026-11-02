import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useRouter, useSearch } from '@tanstack/react-router'
import { Check } from 'lucide-react'
import { useState } from 'react'
import { useForm, useWatch } from 'react-hook-form'
import { z } from 'zod'
import { api, unwrap } from '@/api/client'
import { Button } from '@/components/ui/button'
import { Field, PasswordField } from '@/components/ui/field'
import { safeNext } from '@/lib/safeNext'
import { applyTheme } from '@/lib/theme'
import { AuthLayout } from './AuthLayout'
import { applyProblem } from './formErrors'

const schema = z.object({
  email: z.email('Enter a valid email address.'),
  display_name: z.string().trim().min(1, 'Enter your name.').max(60, 'Use 60 characters or fewer.'),
  password: z
    .string()
    .min(12, 'Use at least 12 characters.')
    .max(128, 'Use 128 characters or fewer.'),
})
type Values = z.infer<typeof schema>

export function RegisterPage() {
  const { next } = useSearch({ from: '/register' })
  const router = useRouter()
  const queryClient = useQueryClient()
  const [formError, setFormError] = useState<string>()
  const [emailTaken, setEmailTaken] = useState(false)
  const {
    register,
    handleSubmit,
    setError,
    control,
    formState: { errors, isSubmitting },
  } = useForm<Values>({ resolver: zodResolver(schema), defaultValues: { password: '' } })
  const longEnough = useWatch({ control, name: 'password' }).length >= 12

  const onSubmit = handleSubmit(async (values) => {
    setFormError(undefined)
    setEmailTaken(false)
    try {
      const user = await unwrap(api.POST('/auth/register', { body: values }))
      queryClient.setQueryData(['me'], user)
      applyTheme(user.preferences.theme ?? 'system')
      await router.navigate({ href: safeNext(next) })
    } catch (e) {
      setEmailTaken(typeof e === 'object' && e !== null && 'code' in e && e.code === 'email_taken')
      setFormError(applyProblem(e, setError, ['email', 'display_name', 'password']))
    }
  })

  return (
    <AuthLayout title="Create your account">
      <form onSubmit={(e) => void onSubmit(e)} noValidate className="space-y-4">
        {formError && (
          <p role="alert" className="rounded-md border border-danger p-3 text-sm text-danger">
            {formError}{' '}
            {emailTaken && (
              <Link to="/login" search={next ? { next } : {}} className="font-medium underline">
                Sign in instead?
              </Link>
            )}
          </p>
        )}
        <Field
          label="Email"
          type="email"
          autoComplete="email"
          error={errors.email?.message}
          {...register('email')}
        />
        <Field
          label="Your name"
          autoComplete="name"
          error={errors.display_name?.message}
          {...register('display_name')}
        />
        <PasswordField
          label="Password"
          autoComplete="new-password"
          error={errors.password?.message}
          hint={
            <span className={longEnough ? 'text-success' : undefined}>
              {longEnough && <Check className="mr-1 inline size-4" aria-hidden="true" />}
              At least 12 characters
            </span>
          }
          {...register('password')}
        />
        <Button type="submit" loading={isSubmitting} className="w-full">
          Create account
        </Button>
      </form>
      <p className="text-center text-sm text-text-muted">
        Already have an account?{' '}
        <Link
          to="/login"
          search={next ? { next } : {}}
          className="font-medium text-accent underline"
        >
          Sign in
        </Link>
      </p>
    </AuthLayout>
  )
}
