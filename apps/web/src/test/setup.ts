import '@testing-library/jest-dom/vitest'
import { cleanup } from '@testing-library/react'
import { toast } from 'sonner'
import { afterAll, afterEach, beforeAll, vi } from 'vitest'
import { server } from './server'

// jsdom has no matchMedia; report "no dark-mode preference" unless a test overrides it.
Object.defineProperty(window, 'matchMedia', {
  writable: true,
  value: (query: string) => ({
    // Desktop width unless a test says otherwise; no dark-mode preference.
    matches: /min-width:\s*768px/.test(query),
    media: query,
    addEventListener: () => undefined,
    removeEventListener: () => undefined,
  }),
})

// Radix UI (selects, popovers, dialogs) expects a few browser APIs that jsdom does not have.
Element.prototype.hasPointerCapture = () => false
Element.prototype.setPointerCapture = () => undefined
Element.prototype.releasePointerCapture = () => undefined
Element.prototype.scrollIntoView = () => undefined
globalThis.ResizeObserver = class {
  observe() {}
  unobserve() {}
  disconnect() {}
}

// React warnings that point at real bugs fail the test that caused them, instead of scrolling past in the log.
const reactProblems: string[] = []
const realError = console.error.bind(console)
vi.spyOn(console, 'error').mockImplementation((...args: unknown[]) => {
  const text = args.map(String).join(' ')
  if (
    /unique "key" prop|validateDOMNesting|cannot be a descendant|cannot contain a nested/.test(text)
  )
    reactProblems.push(text.slice(0, 300))
  realError(...args)
})

beforeAll(() => {
  server.listen({ onUnhandledFrame: 'error' })
})
afterEach(() => {
  const problems = reactProblems.splice(0)
  if (problems.length > 0) throw new Error(`React reported a problem:\n${problems.join('\n')}`)
  cleanup()
  toast.dismiss() // sonner keeps its toasts globally; do not let them leak into the next test
  server.resetHandlers()
  // The browser would keep cookies between page loads; tests start from a clean jar.
  for (const c of document.cookie.split('; ')) {
    const name = c.split('=')[0]
    if (name) document.cookie = `${name}=; expires=Thu, 01 Jan 1970 00:00:00 GMT; path=/`
  }
  localStorage.clear()
  document.documentElement.classList.remove('dark')
})
afterAll(() => {
  server.close()
})
