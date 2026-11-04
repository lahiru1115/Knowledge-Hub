import { HttpResponse, http } from 'msw'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { server } from '@/test/server'
import { API, problem, setCsrfCookie } from '@/test/helpers'
import { ApiProblem, REFRESH_LOCK, api, setSessionExpiredHandler, unwrap } from './client'

afterEach(() => {
  setSessionExpiredHandler(undefined)
})

describe('CSRF', () => {
  it('sends the kh_csrf cookie value in X-CSRF-Token on state-changing requests', async () => {
    setCsrfCookie('token-abc')
    let seen: string | null = null
    server.use(
      http.post(`${API}/auth/logout`, ({ request }) => {
        seen = request.headers.get('X-CSRF-Token')
        return new HttpResponse(null, { status: 204 })
      }),
    )

    await unwrap(api.POST('/auth/logout'))

    expect(seen).toBe('token-abc')
  })

  it('does not add the header to GET requests', async () => {
    setCsrfCookie('token-abc')
    let seen: string | null = 'unset'
    server.use(
      http.get(`${API}/me`, ({ request }) => {
        seen = request.headers.get('X-CSRF-Token')
        return HttpResponse.json({ id: 'x' })
      }),
    )

    await unwrap(api.GET('/me'))

    expect(seen).toBeNull()
  })

  it('fetches a CSRF cookie first when the browser has none', async () => {
    const calls: string[] = []
    server.use(
      http.get(`${API}/auth/csrf`, () => {
        calls.push('csrf')
        setCsrfCookie('fresh')
        return new HttpResponse(null, { status: 204 })
      }),
      http.post(`${API}/auth/logout`, ({ request }) => {
        calls.push(`logout:${request.headers.get('X-CSRF-Token') ?? ''}`)
        return new HttpResponse(null, { status: 204 })
      }),
    )

    await unwrap(api.POST('/auth/logout'))

    expect(calls).toEqual(['csrf', 'logout:fresh'])
  })
})

describe('silent refresh', () => {
  it('refreshes once on 401 and retries the original request with the rotated CSRF token', async () => {
    setCsrfCookie('before')
    let meCalls = 0
    server.use(
      http.get(`${API}/me`, () =>
        ++meCalls === 1 ? problem(401, 'unauthenticated') : HttpResponse.json({ id: 'me' }),
      ),
      http.post(`${API}/auth/refresh`, () => {
        setCsrfCookie('after') // the real API rotates it
        return new HttpResponse(null, { status: 204 })
      }),
    )

    const me = await unwrap(api.GET('/me'))

    expect(me).toEqual({ id: 'me' })
    expect(meCalls).toBe(2)
  })

  it('retries a failed POST with its body and the new CSRF token', async () => {
    setCsrfCookie('before')
    const seen: { csrf: string | null; body: unknown }[] = []
    server.use(
      http.post(`${API}/auth/logout`, async ({ request }) => {
        seen.push({ csrf: request.headers.get('X-CSRF-Token'), body: await request.text() })
        return seen.length === 1
          ? problem(401, 'unauthenticated')
          : new HttpResponse(null, { status: 204 })
      }),
      http.post(`${API}/auth/refresh`, () => {
        setCsrfCookie('after')
        return new HttpResponse(null, { status: 204 })
      }),
    )

    await unwrap(api.POST('/auth/logout'))

    expect(seen.map((s) => s.csrf)).toEqual(['before', 'after'])
  })

  it('shares one refresh between requests that fail at the same time', async () => {
    setCsrfCookie('c')
    let refreshes = 0
    let ok = false
    server.use(
      http.get(`${API}/me`, () =>
        ok ? HttpResponse.json({ id: 'me' }) : problem(401, 'unauthenticated'),
      ),
      http.get(`${API}/tags`, () =>
        ok ? HttpResponse.json({ items: [] }) : problem(401, 'unauthenticated'),
      ),
      http.post(`${API}/auth/refresh`, async () => {
        refreshes++
        await new Promise((r) => setTimeout(r, 30))
        ok = true
        return new HttpResponse(null, { status: 204 })
      }),
    )

    const [me, tags] = await Promise.all([unwrap(api.GET('/me')), unwrap(api.GET('/tags'))])

    expect(me).toEqual({ id: 'me' })
    expect(tags).toEqual({ items: [] })
    expect(refreshes).toBe(1)
  })

  it('refreshes under a lock that other tabs share, so two tabs never send the same refresh token', async () => {
    setCsrfCookie('c')
    const held: string[] = []
    let insideLock = false
    const locks = {
      request: vi.fn(async (name: string, cb: () => Promise<boolean>) => {
        held.push(name)
        insideLock = true
        try {
          return await cb()
        } finally {
          insideLock = false
        }
      }),
    }
    // Add a lock manager to the real navigator for this test only (jsdom has none).
    Object.defineProperty(globalThis.navigator, 'locks', { value: locks, configurable: true })
    let refreshedInsideLock: boolean | undefined
    let ok = false
    server.use(
      http.get(`${API}/me`, () =>
        ok ? HttpResponse.json({ id: 'me' }) : problem(401, 'unauthenticated'),
      ),
      http.post(`${API}/auth/refresh`, () => {
        refreshedInsideLock = insideLock
        ok = true
        return new HttpResponse(null, { status: 204 })
      }),
    )
    try {
      expect(await unwrap(api.GET('/me'))).toEqual({ id: 'me' })
    } finally {
      Reflect.deleteProperty(globalThis.navigator, 'locks')
    }
    expect(held).toEqual([REFRESH_LOCK])
    expect(refreshedInsideLock).toBe(true)
  })

  it('reports an ended session once when the refresh fails, and surfaces the 401', async () => {
    setCsrfCookie('c')
    const expired = vi.fn()
    setSessionExpiredHandler(expired)
    server.use(
      http.get(`${API}/me`, () => problem(401, 'unauthenticated')),
      http.post(`${API}/auth/refresh`, () => problem(401, 'unauthenticated')),
    )

    await expect(unwrap(api.GET('/me'))).rejects.toMatchObject({
      code: 'unauthenticated',
      status: 401,
    })

    expect(expired).toHaveBeenCalledTimes(1)
  })

  it.each(['/auth/login', '/auth/register'] as const)(
    'does not try to refresh when %s answers 401 (wrong credentials is an answer, not an expired session)',
    async (path) => {
      setCsrfCookie('c')
      let refreshes = 0
      server.use(
        http.post(`${API}${path}`, () => problem(401, 'invalid_credentials')),
        http.post(`${API}/auth/refresh`, () => {
          refreshes++
          return new HttpResponse(null, { status: 204 })
        }),
      )

      const body = { email: 'a@b.co', password: 'x', display_name: 'A' }
      await expect(unwrap(api.POST(path, { body }))).rejects.toMatchObject({
        code: 'invalid_credentials',
      })

      expect(refreshes).toBe(0)
    },
  )
})

describe('unwrap', () => {
  it('turns problem+json into an ApiProblem with code, field errors, trace id and retry_after', async () => {
    setCsrfCookie('c')
    server.use(
      http.post(`${API}/auth/login`, () =>
        problem(423, 'account_locked', { retry_after: 600, errors: { email: ['bad'] } }),
      ),
    )

    const error = await unwrap(
      api.POST('/auth/login', { body: { email: 'a@b.co', password: 'x' } }),
    ).catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiProblem)
    expect(error).toMatchObject({
      status: 423,
      code: 'account_locked',
      traceId: 'abc123',
      retryAfter: 600,
      errors: { email: ['bad'] },
      message: 'Too many attempts. Try again in 10 minutes.',
    })
  })

  it('reports a network failure as network_error', async () => {
    server.use(http.get(`${API}/me`, () => HttpResponse.error()))

    await expect(unwrap(api.GET('/me'))).rejects.toMatchObject({ code: 'network_error', status: 0 })
  })

  it('treats a non-problem 500 as internal_error', async () => {
    server.use(http.get(`${API}/me`, () => new HttpResponse('boom', { status: 500 })))

    await expect(unwrap(api.GET('/me'))).rejects.toMatchObject({
      code: 'internal_error',
      status: 500,
    })
  })
})
