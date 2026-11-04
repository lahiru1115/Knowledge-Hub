import { useSyncExternalStore } from 'react'

function subscribe(onChange: () => void) {
  const observer = new MutationObserver(onChange)
  observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] })
  return () => {
    observer.disconnect()
  }
}

/** True while the app is in dark mode (the `dark` class on <html>, which applyTheme toggles). */
export function useIsDark(): boolean {
  return useSyncExternalStore(subscribe, () => document.documentElement.classList.contains('dark'))
}
