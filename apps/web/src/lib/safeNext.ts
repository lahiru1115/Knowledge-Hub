/**
 * Where to go after signing in. Only same-site paths are allowed, so a crafted `?next=` link cannot
 * send a freshly signed-in user to another site, and never back to the auth pages.
 */
export function safeNext(next: string | undefined): string {
  if (!next?.startsWith('/') || next.startsWith('//') || next.startsWith('/\\')) return '/'
  if (/^\/(login|register)(\/|\?|$)/.test(next)) return '/'
  return next
}
