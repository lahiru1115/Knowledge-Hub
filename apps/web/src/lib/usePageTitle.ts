import { useEffect } from 'react'

/** "Library · Knowledge Hub" (WCAG 2.4.2). The shell announces title changes to screen readers. */
export function usePageTitle(title: string): void {
  useEffect(() => {
    document.title = `${title} · Knowledge Hub`
  }, [title])
}
