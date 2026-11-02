# ADR-008: One database, schema per service

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: fits one free database; the boundary is still enforced by permissions. See the quality drivers in Doc 02 §1.

## Decision

`core` and `ai` schemas, four roles, one migration tool per schema.

## Consequences

Both services fail if the database is down; shared capacity.

## Alternatives considered

- Two databases (two free projects, more to run)
