# ADR-009: Zero-cost default AI profile

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: runs with no credit card; privacy by default. See the quality drivers in Doc 02 §1.

## Decision

`AI_PROFILE=free` uses only local components; Claude optional.

## Consequences

Lower quality in free mode; CPU needs for the local model.

## Alternatives considered

- Paid API by default
