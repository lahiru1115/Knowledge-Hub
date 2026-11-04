# ADR-007: Hybrid search

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: vector search misses exact terms and names; keyword search misses meaning. See the quality drivers in Doc 02 §1.

## Decision

Vector + full-text over chunks, merged with reciprocal rank fusion.

## Consequences

Two queries per search.

## Alternatives considered

- Vector only
- keyword only
