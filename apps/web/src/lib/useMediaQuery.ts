import { useSyncExternalStore } from 'react'

/** Tracks a CSS media query, e.g. `useMediaQuery('(min-width: 768px)')`. */
export function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (onChange) => {
      const m = matchMedia(query)
      m.addEventListener('change', onChange)
      return () => {
        m.removeEventListener('change', onChange)
      }
    },
    () => matchMedia(query).matches,
  )
}

export const useIsDesktop = () => useMediaQuery('(min-width: 768px)')
