# ADR-006: Provider-agnostic AI layer

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: swap providers by configuration; test with fakes. See the quality drivers in Doc 02 §1.

## Decision

`Summarizer`, `Tagger`, `Embedder`, `LlmProvider` interfaces with per-profile implementations.

## Consequences

Lowest-common-denominator features.

## Alternatives considered

- Calling one SDK directly
