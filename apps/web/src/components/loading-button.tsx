import type { ComponentProps } from 'react'
import { Button } from '@/components/ui/button'
import { Spinner } from '@/components/ui/spinner'

/** shadcn's Button plus a busy state: disabled, announced as busy, with the Spinner in front of the label. */
export function LoadingButton({
  loading = false,
  disabled,
  children,
  ...props
}: ComponentProps<typeof Button> & { loading?: boolean }) {
  return (
    <Button {...props} disabled={disabled ?? loading} aria-busy={loading || undefined}>
      {loading && <Spinner aria-hidden="true" role="presentation" />}
      {children}
    </Button>
  )
}
