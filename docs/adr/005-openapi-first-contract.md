# ADR-005: OpenAPI-first contract

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: frontend and backend can be built and tested apart; the contract documents the API. See the quality drivers in Doc 02 §1.

## Decision

`contracts/openapi.yaml` written first; generated client; contract tests.

## Consequences

Two artefacts to keep in step (checked by `oasdiff`).

## Alternatives considered

- Code-first OpenAPI (contract drifts with code)
