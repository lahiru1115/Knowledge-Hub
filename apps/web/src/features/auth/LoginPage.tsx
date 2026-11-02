import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useRouter, useSearch } from '@tanstack/react-router'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
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
  password: z.string().min(1, 'Enter your password.'),
})
type Values = z.infer<typeof schema>

export function LoginPage() {
  const { next } = useSearch({ from: '/login' })
  const router = useRouter()
  const queryClient = useQueryClient()
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
      const user = await unwrap(api.POST('/auth/login', { body: values }))
      queryClient.setQueryData(['me'], user)
      applyTheme(user.preferences.theme ?? 'system')
      await router.navigate({ href: safeNext(next) })
    } catch (e) {
      setFormError(applyProblem(e, setError, ['email', 'password']))
    }
  })

  return (
    <AuthLayout title="Sign in">
      <form onSubmit={(e) => void onSubmit(e)} noValidate className="space-y-4">
        {formError && (
          <p role="alert" className="rounded-md border border-danger p-3 text-sm text-danger">
            {formError}
          </p>
        )}
        <Field
          label="Email"
          type="email"
          autoComplete="email"
          error={errors.email?.message}
          {...register('email')}
        />
        <PasswordField
          label="Password"
          autoComplete="current-password"
          error={errors.password?.message}
          {...register('password')}
        />
        <Button type="submit" loading={isSubmitting} className="w-full">
          Sign in
        </Button>
      </form>
      <p className="text-center text-sm text-text-muted">
        New here?{' '}
        <Link
          to="/register"
          search={next ? { next } : {}}
          className="font-medium text-accent underline"
        >
          Create an account
        </Link>
      </p>
    </AuthLayout>
  )
}
