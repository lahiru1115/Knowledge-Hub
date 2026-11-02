import { zodResolver } from '@hookform/resolvers/zod'
import { useSuspenseQuery } from '@tanstack/react-query'
import { useState } from 'react'
import { useForm } from 'react-hook-form'
import { z } from 'zod'
import { Button } from '@/components/ui/button'
import { Field } from '@/components/ui/field'
import { applyProblem } from '@/features/auth/formErrors'
import { meQueryOptions } from '@/features/auth/session'
import { useUpdateMe } from './useUpdateMe'

const schema = z.object({
  display_name: z.string().trim().min(1, 'Enter your name.').max(60, 'Use 60 characters or fewer.'),
})
type Values = z.infer<typeof schema>

export function ProfileSection() {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const update = useUpdateMe()
  const [status, setStatus] = useState<'idle' | 'saved'>('idle')
  const [formError, setFormError] = useState<string>()
  const {
    register,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitting, isDirty },
  } = useForm<Values>({
    resolver: zodResolver(schema),
    defaultValues: { display_name: me.display_name },
  })

  const onSubmit = handleSubmit(async (values) => {
    setStatus('idle')
    setFormError(undefined)
    try {
      const user = await update.mutateAsync({ display_name: values.display_name })
      reset({ display_name: user.display_name })
      setStatus('saved')
    } catch (e) {
      setFormError(applyProblem(e, setError, ['display_name']))
    }
  })

  return (
    <section aria-labelledby="profile-title" className="max-w-md space-y-4">
      <h2 id="profile-title" className="text-xl font-semibold">
        Profile
      </h2>
      <form onSubmit={(e) => void onSubmit(e)} noValidate className="space-y-4">
        {formError && (
          <p role="alert" className="rounded-md border border-danger p-3 text-sm text-danger">
            {formError}
          </p>
        )}
        <Field
          label="Your name"
          autoComplete="name"
          error={errors.display_name?.message}
          {...register('display_name')}
        />
        <Field label="Email" value={me.email} readOnly hint="You can't change your email yet." />
        <div className="flex items-center gap-3">
          <Button type="submit" loading={isSubmitting} disabled={!isDirty}>
            Save changes
          </Button>
          <p role="status" className="text-sm text-success">
            {status === 'saved' ? 'Saved' : ''}
          </p>
        </div>
      </form>
    </section>
  )
}
