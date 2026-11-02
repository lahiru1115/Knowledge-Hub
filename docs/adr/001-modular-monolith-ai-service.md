# ADR-001: Modular monolith + AI service

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: simple to run and debug; one clear service boundary where the technology really differs. See the quality drivers in Doc 02 §1.

## Decision

One ASP.NET Core API with internal modules, plus a separate FastAPI AI service.

## Consequences

Modules must respect boundaries by convention and tests; two languages to maintain.

## Alternatives considered

- Microservices per module (too much overhead)
- AI inside .NET (weaker AI ecosystem)
