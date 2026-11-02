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
- Core API solution skeleton, built and tested (Stage 1).

## Stage 1 — Make the skeleton real (done)

| # | Work | Done when |
|---|---|---|
| 1.1 | Build the .NET solution on a machine with the .NET 10 SDK; fix package versions | `dotnet build` and `dotnet test` pass |
| 1.2 | Review the inferred shapes in `openapi.yaml` (dashboard, AI status and usage, `/auth/*` bodies, bulk request) | Contract reviewed and merged |
| 1.3 | CI: `contract.yml` (lint and validate `openapi.yaml`), `core-api.yml` (build, test), `security.yml` (secret scan) | Green on a pull request |
| 1.4 | `docker compose up` brings up Postgres with the `core` and `ai` schemas and roles | Roles can connect; `core_app` cannot read `ai` |

## Stage 2 — Identity (Core API + web shell)

**Status:** done. Backend (register, login, logout, refresh, `/me`, CSRF, lockout, rate limits, audit, `migrate-core`), the web shell, and the Should items: password reset by email, password change, profile and theme preferences, and account deletion (settings screens included). One piece is deferred: account deletion must also queue the `user.deleted` outbox event so the AI service purges its data (FR-AUTH-09). That is added with the outbox in 4.1 (see below).

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

**Status:** 3.1 to 3.7 done except saved views (Could). The web screens are built: responsive app shell (top bar, sidebar, phone tab bar and Add button), library list with URL-based search, filters, sort and paging, bulk actions, quick-add with duplicate detection, the resource drawer (inline edit, tags, Markdown notes with autosave and conflict handling, summary), collections (list, collection page, create/edit/delete with the move-or-delete choice, Move up/down), the tags manager, toasts with Undo, and single-key shortcuts that can be turned off.

Not built yet, deliberately (each depends on a later stage or is a Could):
- **Needs Stage 5 (AI):** keyword/semantic switch, tag suggestions in quick-add, Summarise and Related in the drawer.
- **Later polish (Should/Could):** grid view, drag and drop onto the sidebar (Move to… and Move up/down are the keyboard alternatives), the ⌘K command palette, search results as you type in the top bar, clipboard paste chip, the drawer beside the list at 1440 px and wider (it overlays today), clipboard-free share target (PWA).

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
| 4.1 | `outbox` table, writer in the same transaction as the change, `BackgroundService` worker with retries and dead-letter. Also emit `user.deleted` from `AccountService.DeleteAccountAsync` (deferred from Stage 2), and move password reset email onto the outbox if in-memory sending proves too fragile | ADR-004, DR-05, FR-AUTH-09 |
| 4.2 | Metadata fetcher: AngleSharp, SmartReader, type guessing from configuration | FR-META-01, 02 |
| 4.3 | SSRF guard with a blocked-URL test suite (private ranges, redirects, DNS rebinding) | FR-META-05, NFR-SEC-07 |
| 4.4 | `POST /metadata/preview`, `POST /resources/{id}/metadata/refresh`, status polling in the web app | FR-META-03, 04 |
| 4.5 | Maintenance service: purge deleted resources after 7 days, expired tokens, old outbox rows | Doc 01 §6.4 |

**Demo:** paste a URL and watch title, image and type fill in.

**Status:** 4.1 to 4.5 done.
- **Outbox (4.1):** events are written in the same transaction as the change (`resource.created|updated|content_changed|deleted|restored`, `user.deleted`). The worker claims with `FOR UPDATE SKIP LOCKED`, retries after 10 s × 2ⁿ with ±20 % jitter, marks a message dead after 8 attempts, takes back claims older than 5 minutes, and carries the trace ID across. The AI events complete at once for now (`AiEventPlaceholderHandler`); Stage 5 replaces that handler and runs a one-off re-index for resources saved before it. Password reset email stays on the in-memory sender, which has been reliable.
- **Fetcher (4.2):** AngleSharp reads the Open Graph, Twitter and standard tags and the favicon; SmartReader reads the main text. Type guessing rules (`Metadata:TypeRules`: host, optional path pattern) fall back to built-in defaults. A user-typed title, description or type is never overwritten, and the job does not touch `updated_at`, so it cannot cause a false edit conflict.
- **SSRF guard (4.3):** `SsrfPolicy` and `SafeConnector`. The connection goes to an address that was just checked, so a name that changes its answer gets no second chance. The blocked-URL suite covers private, loopback, link-local and cloud-metadata ranges, CGNAT, multicast, IPv4-mapped, NAT64 and 6to4 IPv6, numeric spellings, schemes, ports, credentials, and redirects to any of these.
- **Endpoints and web (4.4):** `POST /metadata/preview` (20 a minute per user; a failed fetch is data, not an error) and `POST /resources/{id}/metadata/refresh`. Quick-add shows the preview card and sends a type only when the user picks one. Rows and the drawer show "Fetching details…" and "Couldn't fetch details · Retry"; the app polls every 2 s for up to 30 s after saving or retrying.
- **Maintenance (4.5):** hourly; removes resources deleted over 7 days ago, sign-in tokens 7 days after expiry or revocation, reset tokens after 24 hours, outbox rows done over 7 days and dead over 30 days, audit events over 90 days.

Not done: tag rename, merge and delete do not emit events, so the AI service learns of tag changes only when a resource's content changes (to be reviewed in Stage 5). Fetching real sites cannot be tested without the internet; the fetcher is tested against a local server and the worker with a scripted fetcher, and the browser tests cover the refused-address and unreachable paths.

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

**Status:** 5.1 to 5.7 done.
- **5.1:** FastAPI app (`create_app` factory), `/health/live` and `/health/ready` (ready checks the `ai` schema version), Problem Details errors in the Core API's shape, Alembic migration `0001` for `chunks` (one column for any vector size, partial HNSW index per model, generated `tsv`), `summaries`, `chat_sessions`, `chat_messages`, `usage`, a Dockerfile whose job is to serve only (migrations run as a separate one-shot `alembic upgrade head` as `ai_migrator`), and the `ai-service.yml` workflow (ruff, mypy, pgvector database, migrate, tests, migrate down and up, image build). Tests also check the `ai_app` role can use `ai` but not change it or read `core`.
- **5.2 (AI side):** service JWT check — HS256 only, issuer, audience, expiry, required claims, lifetime at most 120 s; the user comes only from `sub`; `system` tokens need a scope and cannot act as a user. The Core API side: `IAiClient` with a token signer (checked against the Python verifier), 2 retries with jittered backoff, a circuit breaker (5 failures, 30 s), per-call timeouts, and `AiUnavailableException` for the `ai_unavailable` mapping.
- **5.3:** provider protocols (`app/providers/base.py`) and the free profile. `ExtractiveSummarizer`: the description if it is at least 80 characters, otherwise the opening paragraphs, cut at a sentence to at most 600 characters, with `not_enough_content` when there is too little text. `YakeTagger`: up to 5 keyphrases, shaped to the Core API's tag rules; the user's existing tags come first when the word appears in the page or an embedding is at least 0.75 similar. `FastEmbedEmbedder`: `BAAI/bge-small-en-v1.5` (384 dimensions), loaded on first use. `build_providers` picks them from `AI_PROFILE`; the Claude profile raises until 5.9. Model IDs come from `EMBEDDING_MODEL` and `MODEL_CACHE_DIR`.
- **5.4:** `PUT /index/{id}` builds the document (title, description, notes, text up to 200,000 characters), chunks it (about 300 words, 50 overlap, sentence and paragraph boundaries, title on every chunk), embeds in batches of 32 and replaces the resource's chunks for that model in one transaction. Unchanged content is not embedded again (a hash of the whole document), though the collection still follows. `DELETE /index/{id}` removes chunks; `?forget=true` also drops the stored text. `DELETE /users/{id}` (scope `users:purge`) removes every `ai` row of a user. Migration `0002` adds `ai.documents`, which keeps each page's text so a title, notes or collection edit, or a restore, re-indexes without the Core API holding or resending the text. In the Core API, `AiIndexHandler` turns outbox events into these calls; when the AI service is down the message waits and retries. The maintenance service now queues `resource.purged` for each resource it removes, so the stored text is forgotten too. `AiService:Enabled` is off by default.
- **5.5:** the AI service has `POST /summarize` (stored summaries are reused by a hash of title, description and text; the page text comes from the stored document), `POST /suggest-tags` (matching of existing tags falls back to words alone if the embedding model is unavailable) and `GET /usage`. Quotas follow Appendix C (free: 300 summaries, 300 tag suggestions, 500 searches, 100 chat messages a day; Claude profile defaults too, all overridable with `QUOTA_*`). A call reserves its usage row under a per-user, per-operation lock before it runs, so parallel requests cannot pass the limit, and a failed call (including not enough content) gives its reservation back. Over the limit: `429 ai_quota_exceeded` with `reset_at` and `Retry-After`. In the Core API: `POST/DELETE /resources/{id}/summary`, `POST /ai/tag-suggestions`, `GET /ai/status` (cached 10 s), `GET /me/ai-usage`. AI failures map to `503 ai_unavailable` (Retry-After 30), `422 not_enough_content`, `429 ai_quota_exceeded`; tag suggestions instead return an empty list with `ai_available: false`. Automatic summary after a save (FR-AI-03) runs in the index job when the user's switch is on and the summary is empty or extracted; it never replaces a user's summary, writes without touching `updated_at`, and treats a refusal as "no summary" rather than a failed job. Two resilience rules were tightened: calls that spend quota (POST) are never retried automatically, and a 429 or 4xx answer neither retries nor counts toward the circuit breaker, so one user's quota cannot cut AI off for everyone.
- **5.6:** hybrid retrieval in the AI service (`app/retrieval/hybrid.py`): a vector leg (top 50 chunks by cosine distance, using the model's partial HNSW index) and a keyword leg (top 50 by `ts_rank_cd`, query words joined with OR so a question still matches), merged by reciprocal rank fusion (`1 / (60 + rank)`), then grouped to one hit per resource with the best chunk as the snippet. The snippet starts at the sentence holding the first query word, and drops the title line. Every query is filtered by the caller and by the embedding model. `POST /search` counts against the search quota and gives the count back if it fails; `GET /resources/{id}/related` uses the mean of the resource's chunk embeddings. In the Core API: `GET /search/semantic` (hits for deleted, archived or foreign resources are dropped; falls back to keyword search with `fallback: true` when the AI service is down, but a quota refusal is passed on) and `GET /resources/{id}/related` (up to 5; 503 when the AI service is down). A test checks that PostgreSQL can use the HNSW index for the exact query the search runs. Chunks no longer repeat the title in their text, since it is already added as a prefix.
- **5.7:** chat. AI service: `OllamaProvider` (streams `/api/chat`, `num_ctx` 8192, 15 s to the first piece and 120 s overall, `available()` checks the model is pulled), prompt templates (fixed system prompt; sources as numbered `<source>` blocks inside the user turn; last 6 messages of history), and sessions (`/chat/sessions*`) with the stored history, titled from the first question. Retrieval takes the best chunks (up to 8 and about 3,000 tokens), and keeps a chunk only if a query word matched it or its meaning is close enough (`CHAT_MIN_SIMILARITY`, default 0.5); with nothing relevant it does not call the model and says so, with no citations. Guardrails: page text cannot close or open a `<source>` block (the tags are broken with a zero-width character), and a streaming `MarkerFilter` drops any `[n]` that matches no retrieved source, rewrites `[1, 2]` as `[1][2]`, and leaves `a[0]` alone. Quota is reserved before the stream starts (so over-limit is a normal 429), and a stream that does not complete gives it back and saves nothing. Events are `token`, `citations`, `done` or `error`. Core API: `/chat/sessions*` and `POST /chat/sessions/{id}/messages`, which checks the scope collection is the user's, throws problems before the stream starts as normal error responses, then relays the events with `Cache-Control: no-cache`, `X-Accel-Buffering: no` and a comment line every 15 s, replacing each citation's title and address with the user's current ones and removing any that is not their live resource. `/ai/status` now reports `chat_available` from the model check.
- Not verified here, and needing a machine that can run a model: that `llama3.2:3b` follows the prompt, cites correctly and resists injection (the five crafted pages in step 5.8), and the right value for `CHAT_MIN_SIMILARITY`, which was chosen without data. Everything else is tested with a scripted model and with a mock of Ollama's HTTP stream.
- Chat messages are only stored when an answer completes; a failed answer leaves the history as it was, so the web app should keep the question text for a retry (Doc 01 flow 4a).
- Needs pgvector 0.8 or later in deployment: the search sets `hnsw.iterative_scan = relaxed_order` when the server has it, so the user filter cannot starve the result list (Doc 02 §4.3). The local test server has 0.6, which lacks the setting, so that behaviour has not been run here; CI uses `pgvector/pgvector:pg17`.
- Known limits: two events for the same resource can run at once and clash on the unique chunk key; one fails and is retried by the outbox, so the end state is right. The hybrid retrieval query (5.6) is not written yet, so the vector index is only checked for existence and for a cast-and-order query, not for use of the index in a plan.
- Not verified here: the real embedding model has never run, because Hugging Face is unreachable from this environment; the embedder is tested against a stand-in for `fastembed`, and similarity logic with a fake embedder. Whether bge-small really scores existing tags at 0.75 or more is untested, so the threshold may need tuning (the evaluation set in 5.8 is the place). The Docker image build and the workflow itself (no Docker in this environment); the rest runs against a local PostgreSQL 16 with pgvector.

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
