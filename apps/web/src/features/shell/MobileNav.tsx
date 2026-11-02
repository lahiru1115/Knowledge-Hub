import { Link } from '@tanstack/react-router'
import { Home, Library, Plus, Folders } from 'lucide-react'
import { Button } from '@/components/ui/button'
import { useQuickAdd } from '@/features/library/QuickAdd'

const tab = 'flex flex-1 flex-col items-center gap-0.5 py-2 text-xs text-muted-foreground'
const active = { className: 'text-primary font-medium', 'aria-current': 'page' as const }

/** Phone navigation: bottom tab bar and a floating Add button (Doc 03 §4.2). Targets are at least 44 px tall. */
export function MobileNav() {
  const quickAdd = useQuickAdd()
  return (
    <>
      <Button
        size="icon-lg"
        className="fixed right-4 bottom-20 z-30 size-12 rounded-full shadow-lg md:hidden"
        aria-label="Add resource"
        onClick={() => {
          quickAdd.open()
        }}
      >
        <Plus aria-hidden="true" />
      </Button>
      <nav
        aria-label="Tabs"
        className="fixed inset-x-0 bottom-0 z-30 flex min-h-14 border-t border-border bg-background md:hidden"
      >
        <Link to="/" className={tab} activeProps={active} activeOptions={{ exact: true }}>
          <Home className="size-5" aria-hidden="true" />
          Home
        </Link>
        <Link to="/library" className={tab} activeProps={active}>
          <Library className="size-5" aria-hidden="true" />
          Library
        </Link>
        <Link to="/collections" className={tab} activeProps={active}>
          <Folders className="size-5" aria-hidden="true" />
          Collections
        </Link>
      </nav>
    </>
  )
}
