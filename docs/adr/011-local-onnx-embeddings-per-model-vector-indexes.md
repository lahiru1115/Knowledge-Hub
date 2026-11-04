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

## Evaluation results

**Not yet recorded.** The harness exists (`services/ai-service/tests/eval/`, see its README) but has not been run on real models. Until it is, `BAAI/bge-small-en-v1.5` and `llama3.2:3b` are the planned defaults, not measured ones. When it has been run, replace this section with the machine used and a table like this:

| | recall@5 | MRR | hand score | citation precision | refusal | injection resisted | first token p90 |
|---|---|---|---|---|---|---|---|
| bge-small-en-v1.5 + llama3.2:3b | | | | | | | |
| bge-small-en-v1.5 + qwen3:4b | | | | | | | |
| nomic-embed-text-v1.5 + (best chat model) | | | | | | | |
