# ADR-011: Local ONNX embeddings, per-model vector indexes

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-02 |
| **Source** | [Doc 02 — System architecture](../02-system-architecture.md) §13 |

## Context

The driving reasons, from Doc 02 §13: small, fast on CPU, no PyTorch; semantic search works even when Ollama is down; profiles can switch without a schema change. See the quality drivers in Doc 02 §1.

## Decision

`fastembed` with `bge-small-en-v1.5` in-process; one `vector` column with a partial HNSW expression index per model.

## Consequences

Re-index needed when switching; index per model to maintain.

## Alternatives considered

- Embeddings through Ollama (`nomic-embed-text`)
- `sentence-transformers` with PyTorch (heavier image)
- fixed-dimension column
