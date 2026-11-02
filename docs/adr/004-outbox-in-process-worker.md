# ADR-004: Outbox + in-process worker

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: changes and events commit together; no broker to host; retries and dead-letter for free. See the quality drivers in Doc 02 §1.

## Decision

Background work through an outbox table drained by a `BackgroundService`.

## Consequences

Polling latency (≈ 2 s); one worker instance in practice.

## Alternatives considered

- Redis/RabbitMQ queues (extra infrastructure)
- fire-and-forget tasks (lost on crash)
