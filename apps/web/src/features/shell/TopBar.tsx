import { useSuspenseQuery } from '@tanstack/react-query'
import { Link, useNavigate } from '@tanstack/react-router'
import { Keyboard, Menu, Plus, Search, Settings } from 'lucide-react'
import { useState } from 'react'
import { Button } from '@/components/ui/button'
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuLabel,
  DropdownMenuRadioGroup,
  DropdownMenuRadioItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from '@/components/ui/dropdown-menu'
import { Input } from '@/components/ui/input'
import { meQueryOptions, useSignOut } from '@/features/auth/session'
import { useQuickAdd } from '@/features/library/QuickAdd'
import { useUpdateMe } from '@/features/settings/useUpdateMe'
import { notifyError } from '@/lib/toasts'
import { applyTheme, type ThemePreference } from '@/lib/theme'

interface Props {
  onToggleSidebar: () => void
  sidebarCollapsed: boolean
  onShowShortcuts: () => void
}

/** Top bar: sidebar toggle, global search (focus with `/`), Add (`N`) and the account menu (Doc 03 §4.1). */
export function TopBar({ onToggleSidebar, sidebarCollapsed, onShowShortcuts }: Props) {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const navigate = useNavigate()
  const quickAdd = useQuickAdd()
  const signOut = useSignOut()
  const update = useUpdateMe()
  const [query, setQuery] = useState('')
  const theme = me.preferences.theme ?? 'system'

  return (
    <header className="sticky top-0 z-20 flex h-14 items-center gap-2 border-b border-border bg-background px-3">
      <Button
        variant="ghost"
        size="icon"
        className="hidden md:inline-flex"
        aria-label="Toggle sidebar"
        aria-expanded={!sidebarCollapsed}
        onClick={onToggleSidebar}
      >
        <Menu aria-hidden="true" />
      </Button>
      <Link to="/" className="mr-2 truncate text-lg font-semibold">
        Knowledge Hub
      </Link>

      <form
        role="search"
        className="relative ml-auto hidden max-w-md flex-1 md:block"
        onSubmit={(e) => {
          e.preventDefault()
          void navigate({ to: '/library', search: { q: query.trim() || undefined } })
        }}
      >
        <Search
          className="pointer-events-none absolute top-1/2 left-3 size-4 -translate-y-1/2 text-muted-foreground"
          aria-hidden="true"
        />
        <Input
          id="global-search"
          type="search"
          aria-label="Quick search"
          placeholder="Search…  (press /)"
          className="pl-9"
          value={query}
          onChange={(e) => {
            setQuery(e.target.value)
          }}
        />
      </form>

      <div className="ml-auto flex items-center gap-1 md:ml-0">
        <Button asChild variant="ghost" size="icon" className="md:hidden">
          <Link to="/library" aria-label="Search">
            <Search aria-hidden="true" />
          </Link>
        </Button>
        <Button
          onClick={() => {
            quickAdd.open()
          }}
          className="hidden md:inline-flex"
        >
          <Plus aria-hidden="true" /> Add
        </Button>
        {/* Non-modal: a modal menu hides the header (with this very button) from assistive technology while it stays focusable. */}
        <DropdownMenu modal={false}>
          <DropdownMenuTrigger asChild>
            <Button
              variant="outline"
              size="icon"
              className="rounded-full"
              aria-label={`Account menu for ${me.display_name}`}
            >
              <span aria-hidden="true">{me.display_name.trim().charAt(0).toUpperCase()}</span>
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end" className="w-56">
            <DropdownMenuLabel className="font-normal">
              <span className="block truncate font-medium">{me.display_name}</span>
              <span className="block truncate text-xs text-muted-foreground">{me.email}</span>
            </DropdownMenuLabel>
            <DropdownMenuSeparator />
            <DropdownMenuItem asChild>
              <Link to="/settings/profile">
                <Settings /> Settings
              </Link>
            </DropdownMenuItem>
            <DropdownMenuItem asChild className="md:hidden">
              <Link to="/tags">Tags</Link>
            </DropdownMenuItem>
            <DropdownMenuSeparator />
            <DropdownMenuLabel>Theme</DropdownMenuLabel>
            <DropdownMenuRadioGroup
              value={theme}
              onValueChange={(v) => {
                applyTheme(v as ThemePreference)
                update.mutate(
                  { preferences: { theme: v as ThemePreference } },
                  {
                    onError: (e) => {
                      applyTheme(theme)
                      notifyError(e)
                    },
                  },
                )
              }}
            >
              <DropdownMenuRadioItem value="system">System</DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="light">Light</DropdownMenuRadioItem>
              <DropdownMenuRadioItem value="dark">Dark</DropdownMenuRadioItem>
            </DropdownMenuRadioGroup>
            <DropdownMenuSeparator />
            <DropdownMenuItem onSelect={onShowShortcuts}>
              <Keyboard /> Keyboard shortcuts
            </DropdownMenuItem>
            <DropdownMenuItem
              onSelect={() => {
                signOut.mutate()
              }}
            >
              Sign out
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </header>
  )
}
