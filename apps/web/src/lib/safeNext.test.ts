import { describe, expect, it } from 'vitest'
import { safeNext } from './safeNext'

describe('safeNext', () => {
  it.each([
    ['/library?type=video', '/library?type=video'],
    ['/ask/123', '/ask/123'],
    ['/', '/'],
  ])('keeps the same-site path %s', (input, expected) => {
    expect(safeNext(input)).toBe(expected)
  })

  it.each([
    undefined,
    '',
    'https://evil.example/phish',
    '//evil.example',
    '/\\evil.example',
    'javascript:alert(1)',
    'library',
    '/login',
    '/login?next=%2F',
    '/register',
  ])('falls back to home for %s', (input) => {
    expect(safeNext(input)).toBe('/')
  })
})
