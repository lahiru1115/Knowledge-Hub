import { screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '@/test/server'
import { API, libraryDefaults, problem, renderApp, setCsrfCookie, user } from '@/test/helpers'

/** A signed-in browser whose /me reflects PATCH /me, like the real API. */
function signedIn(initial: Record<string, unknown> = {}) {
  setCsrfCookie()
  libraryDefaults()
  let me: Record<string, unknown> = { ...user, ...initial }
  const patches: unknown[] = []
  server.use(
    http.get(`${API}/me`, () => HttpResponse.json(me)),
    http.patch(`${API}/me`, async ({ request }) => {
      const body = (await request.json()) as {
        display_name?: string
        preferences?: Record<string, unknown>
      }
      patches.push(body)
      me = {
        ...me,
        ...(body.display_name ? { display_name: body.display_name } : {}),
        preferences: { ...(me.preferences as object), ...body.preferences },
      }
      return HttpResponse.json(me)
    }),
  )
  return { patches }
}

describe('settings navigation', () => {
  it('opens on Profile and lists the sections', async () => {
    signedIn()
    const { router } = renderApp('/settings')

    expect(await screen.findByRole('heading', { name: 'Profile' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/settings/profile')
    const nav = screen.getByRole('navigation', { name: 'Settings' })
    expect(
      within(nav)
        .getAllByRole('link')
        .map((l) => l.textContent),
    ).toEqual(['Profile', 'Appearance', 'Account'])
    expect(within(nav).getByRole('link', { name: 'Profile' })).toHaveAttribute(
      'aria-current',
      'page',
    )
  })

  it('is linked from the app header', async () => {
    signedIn()
    const { router } = renderApp('/')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('link', { name: 'Settings' }))

    expect(await screen.findByRole('heading', { name: 'Profile' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/settings/profile')
  })
})

describe('profile', () => {
  it('shows the email read-only and saves a new display name', async () => {
    const { patches } = signedIn()
    renderApp('/settings/profile')
    const u = userEvent.setup()
    const name = await screen.findByLabelText('Your name')

    expect(screen.getByLabelText('Email')).toHaveValue('ada@example.com')
    expect(screen.getByLabelText('Email')).toHaveAttribute('readonly')
    expect(screen.getByRole('button', { name: 'Save changes' })).toBeDisabled()

    await u.clear(name)
    await u.type(name, 'Grace Hopper')
    await u.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByText('Saved')).toBeInTheDocument()
    expect(patches).toEqual([{ display_name: 'Grace Hopper' }])
    // The account menu in the top bar shows the new name too.
    expect(
      await screen.findByRole('button', { name: 'Account menu for Grace Hopper' }),
    ).toBeInTheDocument()
  })

  it('validates the name in the browser', async () => {
    const { patches } = signedIn()
    renderApp('/settings/profile')
    const u = userEvent.setup()
    const name = await screen.findByLabelText('Your name')

    await u.clear(name)
    await u.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByText('Enter your name.')).toBeInTheDocument()
    expect(patches).toEqual([])
  })
})

describe('appearance', () => {
  it('applies a theme at once and saves it', async () => {
    const { patches } = signedIn()
    renderApp('/settings/appearance')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('radio', { name: 'Dark' }))

    expect(document.documentElement).toHaveClass('dark')
    await waitFor(() => {
      expect(patches).toEqual([{ preferences: { theme: 'dark' } }])
    })
    expect(screen.getByRole('radio', { name: 'Dark' })).toBeChecked()
    expect(localStorage.getItem('kh_theme')).toBe('dark')
  })

  it('goes back to the previous theme if saving fails, and says so', async () => {
    signedIn()
    server.use(http.patch(`${API}/me`, () => problem(500, 'internal_error')))
    renderApp('/settings/appearance')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('radio', { name: 'Dark' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Something went wrong on our side.')
    expect(document.documentElement).not.toHaveClass('dark')
    expect(screen.getByRole('radio', { name: 'System' })).toBeChecked()
  })

  it('applies the theme saved on the account when the app loads (it follows the user across devices)', async () => {
    signedIn({ preferences: { ...user.preferences, theme: 'dark' } })
    renderApp('/')

    await screen.findByRole('heading', { name: 'Welcome, Ada Lovelace' })

    await waitFor(() => {
      expect(document.documentElement).toHaveClass('dark')
    })
  })
})

describe('account: change password', () => {
  async function fill(current: string, next: string) {
    const u = userEvent.setup()
    await u.type(await screen.findByLabelText('Current password'), current)
    await u.type(screen.getByLabelText('New password'), next)
    await u.click(screen.getByRole('button', { name: 'Change password' }))
  }

  it('sends both passwords and confirms, clearing the fields', async () => {
    signedIn()
    let body: unknown
    server.use(
      http.put(`${API}/me/password`, async ({ request }) => {
        body = await request.json()
        return new HttpResponse(null, { status: 204 })
      }),
    )
    renderApp('/settings/account')

    await fill('old passphrase here', 'a brand new passphrase')

    expect(await screen.findByText(/Password changed/)).toBeInTheDocument()
    expect(body).toEqual({
      current_password: 'old passphrase here',
      new_password: 'a brand new passphrase',
    })
    expect(screen.getByLabelText('Current password')).toHaveValue('')
  })

  it('puts a wrong current password under that field, not in a generic alert', async () => {
    signedIn()
    server.use(http.put(`${API}/me/password`, () => problem(401, 'invalid_credentials')))
    renderApp('/settings/account')

    await fill('wrong wrong wrong', 'a brand new passphrase')

    expect(await screen.findByText("That password isn't right. Try again.")).toBeInTheDocument()
    expect(screen.getByLabelText('Current password')).toHaveAttribute('aria-invalid', 'true')
  })

  it('requires a 12 character new password before calling the API', async () => {
    signedIn()
    let called = false
    server.use(
      http.put(`${API}/me/password`, () => {
        called = true
        return new HttpResponse(null, { status: 204 })
      }),
    )
    renderApp('/settings/account')

    await fill('old passphrase here', 'short')

    expect(await screen.findByText('Use at least 12 characters.')).toBeInTheDocument()
    expect(called).toBe(false)
  })

  it('links to the delete page from a clearly marked danger zone', async () => {
    signedIn()
    renderApp('/settings/account')

    expect(await screen.findByRole('heading', { name: 'Danger zone' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Delete account…' })).toHaveAttribute(
      'href',
      '/settings/account/delete',
    )
  })
})

describe('account: delete', () => {
  it('lists what will be deleted and asks for the password', async () => {
    signedIn()
    renderApp('/settings/account/delete')

    expect(await screen.findByRole('heading', { name: 'Delete your account' })).toBeInTheDocument()
    expect(
      screen.getByText('Everything you saved: resources, notes, collections and tags'),
    ).toBeInTheDocument()
    expect(screen.getByRole('button', { name: 'Delete my account and data' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Cancel' })).toHaveAttribute(
      'href',
      '/settings/account',
    )
  })

  it('does nothing without a password', async () => {
    signedIn()
    let called = false
    server.use(
      http.delete(`${API}/me`, () => {
        called = true
        return new HttpResponse(null, { status: 204 })
      }),
    )
    renderApp('/settings/account/delete')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('button', { name: 'Delete my account and data' }))

    expect(await screen.findByText('Enter your password to confirm.')).toBeInTheDocument()
    expect(called).toBe(false)
  })

  it('refuses a wrong password and keeps the account', async () => {
    signedIn()
    server.use(http.delete(`${API}/me`, () => problem(401, 'invalid_credentials')))
    const { router } = renderApp('/settings/account/delete')
    const u = userEvent.setup()

    await u.type(await screen.findByLabelText('Password'), 'wrong wrong wrong')
    await u.click(screen.getByRole('button', { name: 'Delete my account and data' }))

    expect(await screen.findByText("That password isn't right. Try again.")).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/settings/account/delete')
  })

  it('deletes the account, clears everything cached and returns to sign-in', async () => {
    signedIn()
    let body: unknown
    server.use(
      http.delete(`${API}/me`, async ({ request }) => {
        body = await request.json()
        return new HttpResponse(null, { status: 204 })
      }),
    )
    const { router, queryClient } = renderApp('/settings/account/delete')
    const u = userEvent.setup()
    await screen.findByRole('heading', { name: 'Delete your account' })

    // After deletion the session is gone, so /me answers 401 and refresh fails.
    server.use(
      http.get(`${API}/me`, () => problem(401, 'unauthenticated')),
      http.post(`${API}/auth/refresh`, () => problem(401, 'unauthenticated')),
    )
    await u.type(screen.getByLabelText('Password'), 'a long unusual passphrase')
    await u.click(screen.getByRole('button', { name: 'Delete my account and data' }))

    expect(await screen.findByRole('heading', { name: 'Sign in' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/login')
    expect(body).toEqual({ password: 'a long unusual passphrase' })
    expect(queryClient.getQueryData(['me'])).toBeUndefined()
  })
})
