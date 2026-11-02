import type { components } from './schema'

export type ProblemCode = components['schemas']['ProblemCode']
/** `network_error` is raised by the client when no response arrives; it is not an API code. */
export type ClientProblemCode = ProblemCode | 'network_error'

export interface ProblemContext {
  /** Seconds, from `retry_after` on `account_locked` and `rate_limited`. */
  retryAfter?: number
  /** ISO time, from `reset_at` on `ai_quota_exceeded`. */
  resetAt?: string
}

type Message = string | ((ctx: ProblemContext) => string)

function minutes(seconds: number | undefined): string {
  const m = Math.max(1, Math.ceil((seconds ?? 900) / 60))
  return m === 1 ? '1 minute' : `${m.toString()} minutes`
}

/**
 * User-facing text for each API error code (Doc 03 §10.3). The `Record` type makes the compiler
 * fail when the contract gains a code that has no message here.
 */
const messages: Record<ClientProblemCode, Message> = {
  validation_failed: 'Check the highlighted fields and try again.',
  invalid_token: 'This link has expired or was already used. Request a new one.',
  unauthenticated: 'Your session ended. Sign in again to continue.',
  invalid_credentials: "That email and password don't match. Try again.",
  csrf_failed: 'Something went wrong with this request. Reload the page and try again.',
  forbidden: "You don't have access to this.",
  account_disabled: 'This account has been disabled. Contact the administrator.',
  not_found: "This item doesn't exist or was deleted.",
  email_taken: "There's already an account with this email.",
  collection_name_taken: 'You already have a collection with this name.',
  tag_name_taken: 'You already have a tag with this name.',
  edit_conflict: 'This item changed somewhere else.',
  not_enough_content: "This page doesn't have enough text to summarise.",
  account_locked: (c) => `Too many attempts. Try again in ${minutes(c.retryAfter)}.`,
  rate_limited: (c) =>
    c.retryAfter
      ? `You're going a bit fast. Try again in ${c.retryAfter.toString()} seconds.`
      : "You're going a bit fast. Try again in a moment.",
  ai_quota_exceeded: "You've used today's AI limit. It resets at midnight UTC.",
  internal_error: 'Something went wrong on our side. Try again in a moment.',
  ai_unavailable: 'AI features are unavailable right now. Everything else still works.',
  service_unavailable: 'Something went wrong on our side. Try again in a moment.',
  network_error: "Can't reach the server. Check your connection and try again.",
}

export function messageFor(code: ClientProblemCode, ctx: ProblemContext = {}): string {
  const m = messages[code]
  return typeof m === 'function' ? m(ctx) : m
}
