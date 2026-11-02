import { zodResolver } from '@hookform/resolvers/zod'
import { useQueryClient } from '@tanstack/react-query'
import { Link, useRouter } from '@tanstack/react-router'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { ApiProblem, api, unwrap } from '@/api/client'
import { Button } from '@/components/ui/button'
import { PasswordField } from '@/components/ui/field'
import { applyProblem } from '@/features/auth/formErrors'

const schema = z.object({ password: z.string().min(1, 'Enter your password to confirm.') })
type Values = z.infer<typeof schema>

/** A page, not a small dialog: it says what will be deleted and asks for the password (Doc 03 §5.8). */
export function DeleteAccountPage() {
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
      await unwrap(api.DELETE('/me', { body: values }))
      queryClient.clear()
      await router.navigate({ to: '/login', search: {} })
    } catch (e) {
      if (e instanceof ApiProblem && e.code === 'invalid_credentials') {
        setError('password', { type: 'server', message: "That password isn't right. Try again." })
        return
      }
      setFormError(applyProblem(e, setError, ['password']))
    }
  })

  return (
    <section aria-labelledby="delete-title" className="max-w-md space-y-4">
      <h2 id="delete-title" className="text-xl font-semibold">
        Delete your account
      </h2>
      <p className="text-sm">This permanently deletes:</p>
      <ul className="list-disc space-y-1 pl-5 text-sm">
        <li>Your account and sign-in details</li>
        <li>Everything you saved: resources, notes, collections and tags</li>
        <li>Your search and chat history</li>
      </ul>
      <p className="text-sm text-text-muted">You can&apos;t undo this.</p>
      <form onSubmit={(e) => void onSubmit(e)} noValidate className="space-y-4">
        {formError && (
          <p role="alert" className="rounded-md border border-danger p-3 text-sm text-danger">
            {formError}
          </p>
        )}
        <PasswordField
          label="Password"
          autoComplete="current-password"
          error={errors.password?.message}
          {...register('password')}
        />
        <div className="flex items-center gap-3">
          <Button type="submit" variant="danger" loading={isSubmitting}>
            Delete my account and data
          </Button>
          <Link to="/settings/account" className="text-sm font-medium underline">
            Cancel
          </Link>
        </div>
      </form>
    </section>
  )
}
