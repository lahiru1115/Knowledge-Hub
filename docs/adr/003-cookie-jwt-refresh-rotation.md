# ADR-003: Cookie JWT + refresh rotation

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: tokens not readable by scripts; stateless access checks; theft detection through reuse. See the quality drivers in Doc 02 §1.

## Decision

Short access JWT and rotating refresh token, both in `HttpOnly` cookies, plus CSRF token.

## Consequences

Needs CSRF protection and same-origin hosting.

## Alternatives considered

- Tokens in local storage (XSS risk)
- server sessions only (fine, but less to learn)
