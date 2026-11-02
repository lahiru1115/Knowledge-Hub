# ADR-010: Single-VM demo hosting

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: keeps the AI service private and the local model affordable. See the quality drivers in Doc 02 §1.

## Decision

Core API, AI service, Ollama and Caddy on one free ARM VM; Neon; Vercel.

## Consequences

Single point of failure; idle-reclaim risk; ARM images.

## Alternatives considered

- Container Apps (fallback, §10.2)
- paid PaaS
