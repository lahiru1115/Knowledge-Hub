# ADR-002: PostgreSQL + pgvector

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: one database to run and back up; transactional with metadata; good enough at this scale. See the quality drivers in Doc 02 §1.

## Decision

Store vectors in PostgreSQL.

## Consequences

Vector search tuning limited to pgvector options.

## Alternatives considered

- Qdrant, Pinecone, Weaviate (another system to host and secure)
