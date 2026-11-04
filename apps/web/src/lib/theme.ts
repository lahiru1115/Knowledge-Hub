export type ThemePreference = 'system' | 'light' | 'dark'

/** Applies and remembers the theme. index.html applies the saved one before React loads. */
export function applyTheme(theme: ThemePreference): void {
  const dark =
    theme === 'dark' || (theme === 'system' && matchMedia('(prefers-color-scheme: dark)').matches)
  document.documentElement.classList.toggle('dark', dark)
  try {
    localStorage.setItem('kh_theme', theme)
  } catch {
    // Storage can be unavailable (private windows); the theme still applies for this visit.
  }
}
