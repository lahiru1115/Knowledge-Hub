import { useQuery, useSuspenseQuery } from '@tanstack/react-query'
import { Outlet, useRouterState } from '@tanstack/react-router'
import { useEffect, useState } from 'react'
import { Toaster } from '@/components/Toaster'
import { QuickAddProvider, useQuickAdd } from '@/features/library/QuickAdd'
import { collectionsQuery, tagsQuery } from '@/features/library/queries'
import { MobileNav } from '@/features/shell/MobileNav'
import { ShortcutsDialog } from '@/features/shell/ShortcutsDialog'
import { Sidebar } from '@/features/shell/Sidebar'
import { TopBar } from '@/features/shell/TopBar'
import { useKeyShortcuts } from '@/lib/shortcuts'
import { applyTheme } from '@/lib/theme'
import { meQueryOptions } from './session'

const SIDEBAR_KEY = 'kh_sidebar'

function readCollapsed(): boolean {
  try {
    return localStorage.getItem(SIDEBAR_KEY) === 'collapsed'
  } catch {
    return false
  }
}

/**
 * The signed-in layout (Doc 03 §4): skip link, top bar, sidebar (or bottom tabs on a phone), the page, toasts,
 * the quick-add dialog and the global shortcuts. Route changes are announced to screen readers.
 */
export function AppShell() {
  return (
    <QuickAddProvider>
      <Shell />
    </QuickAddProvider>
  )
}

function Shell() {
  const { data: me } = useSuspenseQuery(meQueryOptions)
  const quickAdd = useQuickAdd()
  const [collapsed, setCollapsed] = useState(readCollapsed)
  const [shortcuts, setShortcuts] = useState(false)
  const [announce, setAnnounce] = useState('')
  const pathname = useRouterState({ select: (s) => s.location.pathname })
  const theme = me.preferences.theme ?? 'system'

  // Warm the data every screen needs.
  useQuery(collectionsQuery)
  useQuery(tagsQuery)

  // The server's preference wins, so the theme follows the user across devices.
  useEffect(() => {
    applyTheme(theme)
  }, [theme])

  // Tell screen readers where they are after a route change (Doc 03 §9.1).
  useEffect(() => {
    const t = setTimeout(() => {
      setAnnounce(document.title)
    }, 150)
    return () => {
      clearTimeout(t)
    }
  }, [pathname])

  const toggleSidebar = () => {
    setCollapsed((c) => {
      try {
        localStorage.setItem(SIDEBAR_KEY, c ? 'expanded' : 'collapsed')
      } catch {
        /* remembered for this visit only */
      }
      return !c
    })
  }

  useKeyShortcuts({
    n: (e) => {
      e.preventDefault()
      quickAdd.open()
    },
    '/': (e) => {
      e.preventDefault()
      // The page's own search box if it has one (the library), otherwise the top bar's.
      const box =
        document.querySelector<HTMLInputElement>('[data-page-search]') ??
        document.getElementById('global-search')
      box?.focus()
    },
    '[': () => {
      toggleSidebar()
    },
    '?': () => {
      setShortcuts(true)
    },
  })

  return (
    <div className="flex min-h-dvh flex-col">
      <a
        href="#main"
        className="sr-only focus:not-sr-only focus:absolute focus:top-2 focus:left-2 focus:z-50 focus:rounded-md focus:bg-background focus:px-3 focus:py-2 focus:ring-2 focus:ring-ring"
      >
        Skip to content
      </a>
      <TopBar
        onToggleSidebar={toggleSidebar}
        sidebarCollapsed={collapsed}
        onShowShortcuts={() => {
          setShortcuts(true)
        }}
      />
      <div className="flex min-h-0 flex-1">
        <Sidebar collapsed={collapsed} />
        <main
          id="main"
          tabIndex={-1}
          className="min-w-0 flex-1 px-4 py-6 pb-24 outline-none md:px-8 md:pb-8"
        >
          <div className="mx-auto max-w-5xl">
            <Outlet />
          </div>
        </main>
      </div>
      <MobileNav />
      <Toaster />
      <div aria-live="polite" role="status" className="sr-only">
        {announce}
      </div>
      {shortcuts && (
        <ShortcutsDialog
          onClose={() => {
            setShortcuts(false)
          }}
        />
      )}
    </div>
  )
}
