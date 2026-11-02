import { Alert, AlertDescription } from '@/components/ui/alert'

/** A form-level error (anything that isn't about one field). Alert announces itself as role="alert". */
export function FormAlert({ children }: { children: React.ReactNode }) {
  return (
    <Alert variant="destructive">
      <AlertDescription>{children}</AlertDescription>
    </Alert>
  )
}
