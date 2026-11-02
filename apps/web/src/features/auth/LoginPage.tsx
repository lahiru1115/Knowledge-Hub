import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useRouter, useSearch } from '@tanstack/react-router'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { api, unwrap } from '@/api/client'
import { FormAlert } from '@/components/FormAlert'
import { LoadingButton } from '@/components/loading-button'
import { FormField, PasswordField } from '@/components/form-field'
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
        {formError && <FormAlert>{formError}</FormAlert>}
        <FormField
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
        <p className="text-right text-sm">
          <Link
            to="/forgot-password"
            className="font-medium text-primary underline underline-offset-4"
          >
            Forgot password?
          </Link>
        </p>
        <LoadingButton type="submit" loading={isSubmitting} className="w-full">
          Sign in
        </LoadingButton>
      </form>
      <p className="text-center text-sm text-muted-foreground">
        New here?{' '}
        <Link
          to="/register"
          search={next ? { next } : {}}
          className="font-medium text-primary underline underline-offset-4"
        >
          Create an account
        </Link>
      </p>
    </AuthLayout>
  )
}
