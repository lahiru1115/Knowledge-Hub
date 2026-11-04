import { screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { HttpResponse, http } from 'msw'
import { describe, expect, it } from 'vitest'
import { server } from '@/test/server'
import { API, problem, renderApp, signedOut } from '@/test/helpers'

describe('forgot password', () => {
  it('is reachable from the sign-in screen', async () => {
    signedOut()
    const { router } = renderApp('/login')
    const u = userEvent.setup()

    await u.click(await screen.findByRole('link', { name: 'Forgot password?' }))

    expect(await screen.findByRole('heading', { name: 'Reset your password' })).toBeInTheDocument()
    expect(router.state.location.pathname).toBe('/forgot-password')
  })

  it('asks for the email and shows the same neutral confirmation', async () => {
    signedOut()
    let body: unknown
    server.use(
      http.post(`${API}/auth/password-reset`, async ({ request }) => {
        body = await request.json()
        return new HttpResponse(null, { status: 202 })
      }),
    )
    renderApp('/forgot-password')
    const u = userEvent.setup()

    await u.type(await screen.findByLabelText('Email'), 'ada@example.com')
    await u.click(screen.getByRole('button', { name: 'Send reset link' }))

    expect(await screen.findByRole('heading', { name: 'Check your email' })).toBeInTheDocument()
    expect(screen.getByRole('status')).toHaveTextContent(
      "If there's an account for ada@example.com",
    )
    expect(screen.getByRole('status')).toHaveTextContent('expires in 30 minutes')
    expect(body).toEqual({ email: 'ada@example.com' })
  })

  it('validates the address in the browser first', async () => {
    signedOut()
    renderApp('/forgot-password')
    const u = userEvent.setup()

    await u.type(await screen.findByLabelText('Email'), 'nope')
    await u.click(screen.getByRole('button', { name: 'Send reset link' }))

    expect(await screen.findByText('Enter a valid email address.')).toBeInTheDocument()
  })

  it('tells the user when they are asking too often', async () => {
    signedOut()
    server.use(
      http.post(`${API}/auth/password-reset`, () =>
        problem(429, 'rate_limited', { retry_after: 600 }),
      ),
    )
    renderApp('/forgot-password')
    const u = userEvent.setup()

    await u.type(await screen.findByLabelText('Email'), 'ada@example.com')
    await u.click(screen.getByRole('button', { name: 'Send reset link' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('Try again in 600 seconds.')
  })
})

describe('reset password', () => {
  async function submit(password: string) {
    const u = userEvent.setup()
    await u.type(await screen.findByLabelText('New password'), password)
    await u.click(screen.getByRole('button', { name: 'Update password' }))
  }

  it('sends the token from the link with the new password, then offers sign-in', async () => {
    signedOut()
    let body: unknown
    server.use(
      http.post(`${API}/auth/password-reset/confirm`, async ({ request }) => {
        body = await request.json()
        return new HttpResponse(null, { status: 204 })
      }),
    )
    renderApp('/reset-password?token=abc123')

    await submit('another long unusual passphrase')

    expect(await screen.findByRole('heading', { name: 'Password updated' })).toBeInTheDocument()
    expect(screen.getByRole('link', { name: 'Sign in' })).toHaveAttribute('href', '/login')
    expect(body).toEqual({ token: 'abc123', new_password: 'another long unusual passphrase' })
  })

  it('shows the password rule and ticks it when met', async () => {
    signedOut()
    renderApp('/reset-password?token=abc123')
    const u = userEvent.setup()
    const field = await screen.findByLabelText('New password')

    expect(field).toHaveAccessibleDescription('At least 12 characters')
    await u.type(field, 'twelve chars!')
    expect(screen.getByText('At least 12 characters')).toHaveClass('text-success')
  })

  it('offers a new link when the link is expired or already used', async () => {
    signedOut()
    server.use(http.post(`${API}/auth/password-reset/confirm`, () => problem(400, 'invalid_token')))
    renderApp('/reset-password?token=old')

    await submit('another long unusual passphrase')

    expect(
      await screen.findByRole('heading', { name: "This link can't be used" }),
    ).toBeInTheDocument()
    expect(screen.getByRole('alert')).toHaveTextContent(
      'This link has expired or was already used. Request a new one.',
    )
    expect(screen.getByRole('link', { name: 'Request a new link' })).toHaveAttribute(
      'href',
      '/forgot-password',
    )
  })

  it('treats a link without a token as unusable', async () => {
    signedOut()
    renderApp('/reset-password')
    expect(
      await screen.findByRole('heading', { name: "This link can't be used" }),
    ).toBeInTheDocument()
  })

  it("shows the server's message under the field when the password is too common", async () => {
    signedOut()
    server.use(
      http.post(`${API}/auth/password-reset/confirm`, () =>
        problem(400, 'validation_failed', {
          errors: { new_password: ['This password is too common.'] },
        }),
      ),
    )
    renderApp('/reset-password?token=abc123')

    await submit('passwordpassword')

    expect(await screen.findByText('This password is too common.')).toBeInTheDocument()
    await waitFor(() => {
      expect(screen.getByLabelText('New password')).toHaveAttribute('aria-invalid', 'true')
    })
  })

  it('rejects a short password in the browser without calling the API', async () => {
    signedOut()
    let called = false
    server.use(
      http.post(`${API}/auth/password-reset/confirm`, () => {
        called = true
        return new HttpResponse(null, { status: 204 })
      }),
    )
    renderApp('/reset-password?token=abc123')

    await submit('short')

    expect(await screen.findByText('Use at least 12 characters.')).toBeInTheDocument()
    expect(called).toBe(false)
  })
})
