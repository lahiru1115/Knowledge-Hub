import createClient, { type Middleware } from 'openapi-fetch'
import type { paths } from './schema'
import { type ClientProblemCode, type ProblemCode, messageFor } from './problems'

/** A parsed `application/problem+json` response (Doc 02 §8.4), or a network failure. */
export class ApiProblem extends Error {
  readonly status: number
  readonly code: ClientProblemCode
  readonly errors: Record<string, string[]>
  readonly traceId?: string
  readonly retryAfter?: number
  readonly resetAt?: string
  /** On a tag rename that clashes: the existing tag to offer a merge into. */
  readonly suggestMergeTagId?: string

  constructor(init: {
    status: number
    code: ClientProblemCode
    errors?: Record<string, string[]>
    traceId?: string
    retryAfter?: number
    resetAt?: string
    suggestMergeTagId?: string
  }) {
    super(messageFor(init.code, init))
    this.name = 'ApiProblem'
    this.status = init.status
    this.code = init.code
    this.errors = init.errors ?? {}
    this.traceId = init.traceId
    this.retryAfter = init.retryAfter
    this.resetAt = init.resetAt
    this.suggestMergeTagId = init.suggestMergeTagId
  }
}

interface ProblemBody {
  code?: ProblemCode
  errors?: Record<string, string[]>
  trace_id?: string
  retry_after?: number
  reset_at?: string
  suggest_merge_tag_id?: string
}

function toProblem(status: number, body: unknown): ApiProblem {
  const p = (typeof body === 'object' && body !== null ? body : {}) as ProblemBody
  return new ApiProblem({
    status,
    code: p.code ?? (status >= 500 ? 'internal_error' : 'validation_failed'),
    errors: p.errors,
    traceId: p.trace_id,
    retryAfter: p.retry_after,
    resetAt: p.reset_at,
    suggestMergeTagId: p.suggest_merge_tag_id,
  })
}

const MUTATING = new Set(['POST', 'PUT', 'PATCH', 'DELETE'])
/** A 401 from these is an answer, not an expired session, so it is never refreshed or retried. */
const NO_REFRESH = ['/auth/login', '/auth/register', '/auth/refresh', '/auth/csrf']

let onSessionExpired: (() => void) | undefined

/** Called once when a refresh fails: the app should clear its cache and go to sign-in. */
export function setSessionExpiredHandler(handler: (() => void) | undefined): void {
  onSessionExpired = handler
}

function readCookie(name: string): string | undefined {
  const match = document.cookie.split('; ').find((c) => c.startsWith(`${name}=`))
  return match ? decodeURIComponent(match.slice(name.length + 1)) : undefined
}

const apiUrl = (path: string) => `${globalThis.location.origin}/api/v1${path}`

/** The browser does not send a CSRF cookie until asked; fetch it before the first state-changing call. */
async function ensureCsrf(): Promise<string> {
  if (!readCookie('kh_csrf')) await globalThis.fetch(apiUrl('/auth/csrf'))
  return readCookie('kh_csrf') ?? ''
}

// One refresh at a time: requests that fail while it runs wait for it (Doc 03 §12.4). The promise is
// dropped as soon as it settles, so a later 401 starts a fresh refresh with the rotated cookie.
// Across tabs too: tabs share the refresh cookie, and two tabs sending the same one would look like a stolen token
// to the server, which then ends every session (FR-AUTH-04). A Web Lock makes the second tab wait; by then the cookie
// holds the token the first tab received, so its refresh succeeds.
let refreshing: Promise<boolean> | undefined

export const REFRESH_LOCK = 'kh-refresh'

async function postRefresh(): Promise<boolean> {
  try {
    const csrf = await ensureCsrf()
    const res = await globalThis.fetch(apiUrl('/auth/refresh'), {
      method: 'POST',
      headers: { 'X-CSRF-Token': csrf },
    })
    return res.ok
  } catch {
    return false
  }
}

function refreshOnce(): Promise<boolean> {
  refreshing ??= (async () => {
    try {
      const locks = (globalThis.navigator as Navigator | undefined)?.locks
      return locks ? await locks.request(REFRESH_LOCK, postRefresh) : await postRefresh()
    } finally {
      refreshing = undefined
    }
  })()
  return refreshing
}

const copies = new WeakMap<Request, Request>()

const middleware: Middleware = {
  async onRequest({ request }) {
    if (MUTATING.has(request.method)) request.headers.set('X-CSRF-Token', await ensureCsrf())
    copies.set(request, request.clone())
    return request
  },
  async onResponse({ request, response }) {
    if (response.status !== 401) return undefined
    if (NO_REFRESH.some((p) => new URL(request.url).pathname.endsWith(p))) return undefined

    const original = copies.get(request)
    if (!(await refreshOnce()) || !original) {
      onSessionExpired?.()
      return undefined
    }
    // Refreshing rotates the CSRF cookie, so send the new value on the retry.
    const retry = new Request(original)
    if (MUTATING.has(retry.method)) retry.headers.set('X-CSRF-Token', readCookie('kh_csrf') ?? '')
    return globalThis.fetch(retry)
  },
}

export const api = createClient<paths>({
  baseUrl: apiUrl(''),
  // Resolved at call time so tests can intercept it.
  fetch: (request) => globalThis.fetch(request),
})
api.use(middleware)

/** Resolves with the response data, or throws an {@link ApiProblem}. */
export async function unwrap<T>(
  call: Promise<{ data?: T; error?: unknown; response: Response }>,
): Promise<T> {
  let result
  try {
    result = await call
  } catch {
    throw new ApiProblem({ status: 0, code: 'network_error' })
  }
  if (!result.response.ok) throw toProblem(result.response.status, result.error)
  return result.data as T
}
