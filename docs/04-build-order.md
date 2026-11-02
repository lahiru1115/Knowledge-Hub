# Knowledge Hub — Build Order

| | |
|---|---|
| **Document** | 04 — Build order |
| **Version** | 0.1 (draft) |
| **Date** | 2026-11-02 |
| **Status** | Draft for review |
| **Companion documents** | [01 — SRS](01-software-requirements-specification.md) · [02 — System architecture](02-system-architecture.md) · [03 — UI/UX design plan](03-ui-ux-design-plan.md) |

The order follows the dependencies in Doc 02 and the priorities in Doc 01 (**M**ust, **S**hould, **C**ould). Each stage ends with something that runs and can be demonstrated. Do Must items first; Should and Could items are grouped at the end of the stage that needs them, and can be postponed without blocking later stages.

Rule for every stage: if it adds or changes an endpoint, update `contracts/openapi.yaml` first (ADR-005), then build.

## Stage 0 — Foundation (done)

- Monorepo folders, documents in `docs/`, 11 ADRs in `docs/adr/`.
- `contracts/openapi.yaml` (57 operations) and `schemathesis.toml`.
- `compose.yaml` (infrastructure only), `infra/db/init.sql`, `.env.example`.
- Core API solution skeleton (written but **not yet built**; see Stage 1).

## Stage 1 — Make the skeleton real

| # | Work | Done when |
|---|---|---|
| 1.1 | Build the .NET solution on a machine with the .NET 10 SDK; fix package versions | `dotnet build` and `dotnet test` pass |
| 1.2 | Review the inferred shapes in `openapi.yaml` (dashboard, AI status and usage, `/auth/*` bodies, bulk request) | Contract reviewed and merged |
| 1.3 | CI: `contract.yml` (lint and validate `openapi.yaml`), `core-api.yml` (build, test), `security.yml` (secret scan) | Green on a pull request |
| 1.4 | `docker compose up` brings up Postgres with the `core` and `ai` schemas and roles | Roles can connect; `core_app` cannot read `ai` |

## Stage 2 — Identity (Core API + web shell)

Everything else needs a signed-in user, so this comes first.

| # | Work | Requirements |
|---|---|---|
| 2.1 | EF Core `core` schema: `users`, `refresh_tokens`, `password_reset_tokens`, `audit_events`; first migration; `migrate-core` one-shot in Compose | DR-01, ADR-008 |
| 2.2 | Problem Details mapping for the error catalogue (Doc 02 §8.4); validation with FluentValidation | NFR-API errors |
| 2.3 | CSRF (`/auth/csrf`), register, login, logout, refresh with rotation and reuse detection, `/me` | FR-AUTH-01 to 05 |
| 2.4 | Lockout, rate limiting, Argon2id hashing, `ICurrentUser`, security stamp | FR-AUTH-02, NFR-SEC-06 |
| 2.5 | Web app: Vite + React + TanStack Router, generated API client (`openapi-typescript`), sign-in and register screens, silent refresh | Doc 03 |
| 2.6 | `web.yml` CI (lint, typecheck, test, client up to date, build) | Doc 02 §10.3 |
| 2.7 | Should: password reset and change, profile update, account deletion (Mailpit for email) | FR-AUTH-06 to 09 |

**Demo:** register, sign in, reload, stay signed in, sign out.

## Stage 3 — Library (the core product)

| # | Work | Requirements |
|---|---|---|
| 3.1 | `collections` with counts, Inbox, delete-with-choice; pin and reorder (S) | FR-COL-* |
| 3.2 | `resources` CRUD, types, soft delete and restore, notes, favourites, archive, optimistic concurrency (`expected_updated_at`) | FR-RES-01 to 09 |
| 3.3 | `tags`: create on use, list with counts, rename, merge, delete | FR-TAG-* |
| 3.4 | Keyword search: `search_vector`, filters, sort, pagination on `GET /resources` | FR-SRCH-01 to 04 |
| 3.5 | Duplicate check, bulk actions, saved views (C) | FR-RES-11, 12, FR-SRCH-05 |
| 3.6 | Web: library list, filters, resource drawer, quick-add, collections sidebar, tag management | Doc 03 |
| 3.7 | Integration tests with Testcontainers for owner isolation (`not_found` for other users' IDs) | NFR-SEC-05 |

**Demo:** a usable bookmark manager with search, with no metadata or AI yet.

## Stage 4 — Outbox and metadata

| # | Work | Requirements |
|---|---|---|
| 4.1 | `outbox` table, writer in the same transaction as the change, `BackgroundService` worker with retries and dead-letter | ADR-004, DR-05 |
| 4.2 | Metadata fetcher: AngleSharp, SmartReader, type guessing from configuration | FR-META-01, 02 |
| 4.3 | SSRF guard with a blocked-URL test suite (private ranges, redirects, DNS rebinding) | FR-META-05, NFR-SEC-07 |
| 4.4 | `POST /metadata/preview`, `POST /resources/{id}/metadata/refresh`, status polling in the web app | FR-META-03, 04 |
| 4.5 | Maintenance service: purge deleted resources after 7 days, expired tokens, old outbox rows | Doc 01 §6.4 |

**Demo:** paste a URL and watch title, image and type fill in.

## Stage 5 — AI service

| # | Work | Requirements |
|---|---|---|
| 5.1 | FastAPI skeleton, health, Alembic `ai` schema (`chunks`, `summaries`, `chat_*`, `usage`), `migrate-ai` one-shot, `ai-service.yml` CI | ADR-008 |
| 5.2 | Service-to-service auth and the Core API's `IAiClient` with resilience pipeline; `ai_unavailable` mapping | Doc 02 §5.2, 5.5, 5.6 |
| 5.3 | Provider interfaces and free profile: extractive summariser, YAKE tagger, `fastembed` embeddings | ADR-006, 009, 011 |
| 5.4 | Chunker, indexing from outbox events, per-model HNSW index, user-data purge on `user.deleted` | FR-AI-00, DR-06 |
| 5.5 | Summaries (on demand and after save), tag suggestions, `/ai/status`, quotas and usage metering | FR-AI-02 to 04, 09, 10 |
| 5.6 | Hybrid retrieval (vector + full text, rank fusion): semantic search with keyword fallback, related resources (S) | ADR-007, FR-AI-05, 07 |
| 5.7 | Chat: Ollama provider, prompt templates, citation check, SSE relay, chat history (S) | FR-AI-06, 08, §4.4, §8.5 |
| 5.8 | Model evaluation set in `tests/eval/` | Doc 02 §4.5 |
| 5.9 | Optional Claude and Voyage providers; AI preferences (S) | FR-AI-01, 11, 12 |
| 5.10 | Web: summary panel, tag suggestions, semantic search, "Ask your library" | Doc 03 |

**Demo:** semantic search finds a synonym; chat answers with citations.

## Stage 6 — Insights, export, admin

| # | Work | Requirements |
|---|---|---|
| 6.1 | `GET /dashboard` and the home screen | FR-DASH-* |
| 6.2 | `GET /export` (JSON, CSV) | FR-EXP-* |
| 6.3 | Admin endpoints and screens (C) | FR-ADM-* |

## Stage 7 — Hardening and release

| # | Work | Requirements |
|---|---|---|
| 7.1 | Schemathesis in `contract.yml` against the composed stack; `oasdiff` check of the generated OpenAPI against the contract | Doc 02 §8.1 |
| 7.2 | Playwright E2E smoke: sign in, add, search, ask | Doc 02 §10.3 |
| 7.3 | Security headers, CSRF and cookie review, dependency scan, threat-model walk-through | Doc 02 §9 |
| 7.4 | Observability: OpenTelemetry traces across request, outbox job and AI call; RED metrics; log redaction | Doc 02 §11 |
| 7.5 | Accessibility and performance checks against the NFRs | Doc 01 §5 |
| 7.6 | Demo deployment: `infra/deploy/` (compose, Caddyfile), `deploy.yml`, Vercel config. Test SSE through the Vercel rewrite early (AR-03) | Doc 02 §10.2 |
| 7.7 | Re-check free-tier terms (AR-01, OI-08); choose an email provider (OI-04) | Doc 02 §14 |

## Cross-cutting checks

- **Every stage:** update the traceability matrix in Doc 01 when a requirement is implemented; add tests named in the matrix.
- **Risks to test early:** SSE through Vercel (AR-03) in Stage 5, Oracle idle reclaim and ARM image builds (AR-01, OI-08) before Stage 7.
- **Order flexibility:** the web screens for a stage can lag the API by one stage. Stages 6 and 4 are independent of each other and can swap.
