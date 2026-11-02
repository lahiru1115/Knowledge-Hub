import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '@/test/server'
import { API, problem, renderApp, setCsrfCookie, signedOut, user } from '@/test/helpers'

/** A browser with a valid session: /me answers, logout works. */
function signedIn() {
  setCsrfCookie()
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(user)),
    http.post(`${API}/auth/logout`, () => new HttpResponse(null, { status: 204 })),
  )
}

async function fillLogin(email = 'ada@example.com', password = 'a long unusual passphrase') {
  const u = userEvent.setup()
  await u.type(await screen.findByLabelText('Email'), email)
  await u.type(screen.getByLabelText('Password'), password)
  await u.click(screen.getByRole('button', { name: 'Sign in' }))
  return u
}

describe('route guard', () => {
  it('sends a signed-out visitor to sign-in and remembers where they were going', async () => {
    signedOut()

    const { router } = renderApp('/')

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(router.state.location.search).toEqual({ next: '/' })
  })

  it('shows the app to a signed-in user', async () => {
    signedIn()

    renderApp('/')

    expect(
      await screen.findByRole('heading', { name: 'Welcome, Ada Lovelace' }),
    ).toBeInTheDocument()
  })

  it('refreshes silently when the access token has expired, with no sign-in screen', async () => {
    setCsrfCookie()
    let meCalls = 0
    server.use(
      http.get(`${API}/me`, () =>
        ++meCalls === 1 ? problem(401, 'unauthenticated') : HttpResponse.json(user),
      ),
      http.post(`${API}/auth/refresh`, () => new HttpResponse(null, { status: 204 })),
    )

    renderApp('/')

    expect(
      await screen.findByRole('heading', { name: 'Welcome, Ada Lovelace' }),
    ).toBeInTheDocument()
    expect(meCalls).toBe(2)
  })

  it('skips the sign-in form for someone who is already signed in', async () => {
    signedIn()

    const { router } = renderApp('/login')

    await screen.findByRole('heading', { name: 'Welcome, Ada Lovelace' })
    expect(router.state.location.pathname).toBe('/')
  })
})

describe('sign in', () => {
  it('signs in, shows the app, and returns to the page that was requested', async () => {
    signedOut()
    server.use(http.post(`${API}/auth/login`, () => HttpResponse.json(user)))
    server.use(http.get(`${API}/me`, () => problem(401, 'unauthenticated')))

    const { router } = renderApp('/login?next=%2F')
    await fillLogin()

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/')
    })
  })

  it('sends the entered credentials as JSON with the CSRF header', async () => {
    signedOut()
    let received: { body: unknown; csrf: string | null } | undefined
    server.use(
      http.post(`${API}/auth/login`, async ({ request }) => {
        received = { body: await request.json(), csrf: request.headers.get('X-CSRF-Token') }
        return HttpResponse.json(user)
      }),
    )

    renderApp('/login')
    await fillLogin('ada@example.com', 'hunter2 hunter2')

    await waitFor(() => {
      expect(received).toEqual({
        body: { email: 'ada@example.com', password: 'hunter2 hunter2' },
        csrf: 'csrf-1',
      })
    })
  })

  it('does not follow an off-site next link after signing in', async () => {
    signedOut()
    server.use(http.post(`${API}/auth/login`, () => HttpResponse.json(user)))

    const { router } = renderApp('/login?next=https%3A%2F%2Fevil.example%2F')
    await fillLogin()

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/')
    })
    expect(globalThis.location.hostname).not.toBe('evil.example')
  })

  it('shows the message for wrong credentials in an alert and keeps the form', async () => {
    signedOut()
    server.use(http.post(`${API}/auth/login`, () => problem(401, 'invalid_credentials')))

    renderApp('/login')
    await fillLogin()

    expect(await screen.findByRole('alert')).toHaveTextContent(
      "That email and password don't match. Try again.",
    )
    expect(screen.getByLabelText('Email')).toHaveValue('ada@example.com')
  })

  it('tells a locked-out user how long to wait', async () => {
    signedOut()
    server.use(
      http.post(`${API}/auth/login`, () => problem(423, 'account_locked', { retry_after: 840 })),
    )

    renderApp('/login')
    await fillLogin()

    expect(await screen.findByRole('alert')).toHaveTextContent('Try again in 14 minutes.')
  })

  it('explains a disabled account', async () => {
    signedOut()
    server.use(http.post(`${API}/auth/login`, () => problem(403, 'account_disabled')))

    renderApp('/login')
    await fillLogin()

    expect(await screen.findByRole('alert')).toHaveTextContent('This account has been disabled.')
  })

  it('validates in the browser before calling the API, and marks the fields invalid', async () => {
    signedOut()
    let called = false
    server.use(
      http.post(`${API}/auth/login`, () => {
        called = true
        return HttpResponse.json(user)
      }),
    )

    renderApp('/login')
    const u = userEvent.setup()
    await u.type(await screen.findByLabelText('Email'), 'not-an-email')
    await u.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('Enter a valid email address.')).toBeInTheDocument()
    expect(screen.getByLabelText('Email')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.getByLabelText('Email')).toHaveAccessibleDescription(
      'Enter a valid email address.',
    )
    expect(called).toBe(false)
  })

  it('lets the user reveal the password, and uses the right autocomplete hints', async () => {
    signedOut()
    renderApp('/login')
    const u = userEvent.setup()
    const password = await screen.findByLabelText('Password')

    expect(password).toHaveAttribute('type', 'password')
    expect(password).toHaveAttribute('autocomplete', 'current-password')
    expect(screen.getByLabelText('Email')).toHaveAttribute('autocomplete', 'email')

    await u.click(screen.getByRole('button', { name: 'Show password' }))
    expect(password).toHaveAttribute('type', 'text')
    expect(screen.getByRole('button', { name: 'Hide password' })).toHaveAttribute(
      'aria-pressed',
      'true',
    )
  })
})

describe('register', () => {
  async function fillRegister(
    email = 'ada@example.com',
    name = 'Ada Lovelace',
    password = 'a long unusual passphrase',
  ) {
    const u = userEvent.setup()
    await u.type(await screen.findByLabelText('Email'), email)
    await u.type(screen.getByLabelText('Your name'), name)
    await u.type(screen.getByLabelText('Password'), password)
    await u.click(screen.getByRole('button', { name: 'Create account' }))
  }

  it('shows the password rule up front and ticks it once it is met', async () => {
    signedOut()
    renderApp('/register')
    const u = userEvent.setup()
    const password = await screen.findByLabelText('Password')

    expect(password).toHaveAccessibleDescription('At least 12 characters')
    expect(password).toHaveAttribute('autocomplete', 'new-password')

    await u.type(password, 'twelve chars!')
    expect(screen.getByText('At least 12 characters')).toHaveClass('text-success')
  })

  it('creates the account and goes to the app', async () => {
    signedOut()
    let body: unknown
    server.use(
      http.post(`${API}/auth/register`, async ({ request }) => {
        body = await request.json()
        return HttpResponse.json(user, { status: 201 })
      }),
    )

    const { router } = renderApp('/register')
    await fillRegister()

    await waitFor(() => {
      expect(router.state.location.pathname).toBe('/')
    })
    expect(body).toEqual({
      email: 'ada@example.com',
      display_name: 'Ada Lovelace',
      password: 'a long unusual passphrase',
    })
  })

  it('rejects a short password in the browser', async () => {
    signedOut()
    renderApp('/register')
    await fillRegister('ada@example.com', 'Ada', 'short')

    expect(await screen.findByText('Use at least 12 characters.')).toBeInTheDocument()
  })

  it('offers sign-in when the email is already registered', async () => {
    signedOut()
    server.use(http.post(`${API}/auth/register`, () => problem(409, 'email_taken')))

    renderApp('/register')
    await fillRegister()

    const alert = await screen.findByRole('alert')
    expect(alert).toHaveTextContent("There's already an account with this email.")
    expect(screen.getByRole('link', { name: 'Sign in instead?' })).toHaveAttribute('href', '/login')
  })

  it("puts the server's field messages under the matching fields", async () => {
    signedOut()
    server.use(
      http.post(`${API}/auth/register`, () =>
        problem(400, 'validation_failed', {
          errors: { password: ['This password is too common.'] },
        }),
      ),
    )

    renderApp('/register')
    await fillRegister('ada@example.com', 'Ada', 'passwordpassword')

    expect(await screen.findByText('This password is too common.')).toBeInTheDocument()
    expect(screen.getByLabelText('Password')).toHaveAttribute('aria-invalid', 'true')
    expect(screen.queryByRole('alert')).not.toBeInTheDocument()
  })
})

describe('sign out', () => {
  it('signs out, clears cached data and returns to sign-in', async () => {
    signedIn()
    const { router, queryClient } = renderApp('/')
    const u = userEvent.setup()
    await screen.findByRole('heading', { name: 'Welcome, Ada Lovelace' })

    server.use(http.get(`${API}/me`, () => problem(401, 'unauthenticated')))
    server.use(http.post(`${API}/auth/refresh`, () => problem(401, 'unauthenticated')))
    await u.click(screen.getByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(queryClient.getQueryData(['me'])).toBeUndefined()
  })
})
