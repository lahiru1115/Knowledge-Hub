import { Toaster as Sonner } from 'sonner'
import { useIsDark } from '@/lib/useIsDark'

/** shadcn/ui's toast styling on top of sonner, following the app theme (not the OS one). */
export function Toaster() {
  const dark = useIsDark()
  return (
    <Sonner
      theme={dark ? 'dark' : 'light'}
      position="bottom-right"
      closeButton
      style={
        {
          '--normal-bg': 'var(--popover)',
          '--normal-text': 'var(--popover-foreground)',
          '--normal-border': 'var(--border)',
          // An open drawer or dialog makes everything outside it inert; Undo must still be clickable.
          pointerEvents: 'auto',
        } as React.CSSProperties
      }
    />
  )
}
