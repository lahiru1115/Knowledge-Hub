import { describe, expect, it } from 'vitest'
import { messageFor } from './problems'

describe('messageFor', () => {
  it('rounds the lockout wait up to whole minutes', () => {
    expect(messageFor('account_locked', { retryAfter: 61 })).toBe(
      'Too many attempts. Try again in 2 minutes.',
    )
    expect(messageFor('account_locked', { retryAfter: 30 })).toBe(
      'Too many attempts. Try again in 1 minute.',
    )
    expect(messageFor('account_locked', {})).toBe('Too many attempts. Try again in 15 minutes.')
  })

  it('says how many seconds to wait when rate limited', () => {
    expect(messageFor('rate_limited', { retryAfter: 42 })).toBe(
      "You're going a bit fast. Try again in 42 seconds.",
    )
    expect(messageFor('rate_limited')).toBe("You're going a bit fast. Try again in a moment.")
  })

  it('uses the Doc 03 §10.3 wording for sign-in errors', () => {
    expect(messageFor('invalid_credentials')).toBe(
      "That email and password don't match. Try again.",
    )
    expect(messageFor('account_disabled')).toBe(
      'This account has been disabled. Contact the administrator.',
    )
  })

  it('never leaks server text: unknown failures use the generic message', () => {
    expect(messageFor('internal_error')).toBe(
      'Something went wrong on our side. Try again in a moment.',
    )
    expect(messageFor('service_unavailable')).toBe(messageFor('internal_error'))
  })
})
