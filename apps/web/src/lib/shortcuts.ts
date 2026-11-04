import { useEffect, useSyncExternalStore } from 'react'

const KEY = 'kh_shortcuts'
const listeners = new Set<() => void>()

function read(): boolean {
  try {
    return localStorage.getItem(KEY) !== 'off'
  } catch {
    return true
  }
}

/** Single-key shortcuts can be turned off (WCAG 2.1.4). Stored per device, in Settings → Appearance. */
export function setShortcutsEnabled(on: boolean): void {
  try {
    localStorage.setItem(KEY, on ? 'on' : 'off')
  } catch {
    // Storage can be unavailable; the choice then lasts until reload only.
  }
  listeners.forEach((l) => {
    l()
  })
}

export function useShortcutsEnabled(): boolean {
  return useSyncExternalStore((cb) => {
    listeners.add(cb)
    return () => {
      listeners.delete(cb)
    }
  }, read)
}

/** True when a key press should be left alone: the user is typing, or a dialog or menu has the focus. */
export function isTypingContext(e: KeyboardEvent, allowInDialog = false): boolean {
  const t = e.target
  if (t instanceof HTMLElement) {
    if (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName)) return true
    const blocked = allowInDialog
      ? '[role="menu"], [role="listbox"], [role="combobox"]'
      : '[role="dialog"], [role="alertdialog"], [role="menu"], [role="listbox"], [role="combobox"]'
    if (t.closest(blocked)) return true
  }
  return e.ctrlKey || e.metaKey || e.altKey
}

/**
 * Registers single-key shortcuts while `enabled` (the user setting) is true. `allowInDialog` is for shortcuts that
 * belong to a dialog itself, such as the drawer's J and K.
 */
export function useKeyShortcuts(
  handlers: Record<string, (e: KeyboardEvent) => void>,
  enabled = true,
  allowInDialog = false,
): void {
  const on = useShortcutsEnabled()
  useEffect(() => {
    if (!on || !enabled) return
    const listener = (e: KeyboardEvent) => {
      if (isTypingContext(e, allowInDialog)) return
      const h = handlers[e.key] ?? handlers[e.key.toLowerCase()]
      if (h) h(e)
    }
    document.addEventListener('keydown', listener)
    return () => {
      document.removeEventListener('keydown', listener)
    }
  }, [handlers, on, enabled, allowInDialog])
}

/**
 * Escape closes a dialog, but not while it is cancelling something smaller inside it (an inline edit, an open
 * tag list). Those inputs carry `data-escape-local`, and the dialog's `onEscapeKeyDown` calls this.
 */
export function keepOpenForLocalEscape(e: KeyboardEvent): void {
  if (e.target instanceof HTMLElement && e.target.closest('[data-escape-local]')) e.preventDefault()
}
