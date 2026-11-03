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
- **Stage 5 (AI), done in 5.10:** the keyword/semantic switch, tag suggestions in quick-add, Summarise and Related in the drawer.
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

**Status:** 5.1 to 5.10 done as code. The measurements in 5.8 have not been run, and nothing has been tried against the real Claude or Voyage APIs.
- **5.1:** FastAPI app (`create_app` factory), `/health/live` and `/health/ready` (ready checks the `ai` schema version), Problem Details errors in the Core API's shape, Alembic migration `0001` for `chunks` (one column for any vector size, partial HNSW index per model, generated `tsv`), `summaries`, `chat_sessions`, `chat_messages`, `usage`, a Dockerfile whose job is to serve only (migrations run as a separate one-shot `alembic upgrade head` as `ai_migrator`), and the `ai-service.yml` workflow (ruff, mypy, pgvector database, migrate, tests, migrate down and up, image build). Tests also check the `ai_app` role can use `ai` but not change it or read `core`.
- **5.2 (AI side):** service JWT check — HS256 only, issuer, audience, expiry, required claims, lifetime at most 120 s; the user comes only from `sub`; `system` tokens need a scope and cannot act as a user. The Core API side: `IAiClient` with a token signer (checked against the Python verifier), 2 retries with jittered backoff, a circuit breaker (5 failures, 30 s), per-call timeouts, and `AiUnavailableException` for the `ai_unavailable` mapping.
- **5.3:** provider protocols (`app/providers/base.py`) and the free profile. `ExtractiveSummarizer`: the description if it is at least 80 characters, otherwise the opening paragraphs, cut at a sentence to at most 600 characters, with `not_enough_content` when there is too little text. `YakeTagger`: up to 5 keyphrases, shaped to the Core API's tag rules; the user's existing tags come first when the word appears in the page or an embedding is at least 0.75 similar. `FastEmbedEmbedder`: `BAAI/bge-small-en-v1.5` (384 dimensions), loaded on first use. `build_providers` picks them from `AI_PROFILE`; the Claude profile raises until 5.9. Model IDs come from `EMBEDDING_MODEL` and `MODEL_CACHE_DIR`.
- **5.4:** `PUT /index/{id}` builds the document (title, description, notes, text up to 200,000 characters), chunks it (about 300 words, 50 overlap, sentence and paragraph boundaries, title on every chunk), embeds in batches of 32 and replaces the resource's chunks for that model in one transaction. Unchanged content is not embedded again (a hash of the whole document), though the collection still follows. `DELETE /index/{id}` removes chunks; `?forget=true` also drops the stored text. `DELETE /users/{id}` (scope `users:purge`) removes every `ai` row of a user. Migration `0002` adds `ai.documents`, which keeps each page's text so a title, notes or collection edit, or a restore, re-indexes without the Core API holding or resending the text. In the Core API, `AiIndexHandler` turns outbox events into these calls; when the AI service is down the message waits and retries. The maintenance service now queues `resource.purged` for each resource it removes, so the stored text is forgotten too. `AiService:Enabled` is off by default.
- **5.5:** the AI service has `POST /summarize` (stored summaries are reused by a hash of title, description and text; the page text comes from the stored document), `POST /suggest-tags` (matching of existing tags falls back to words alone if the embedding model is unavailable) and `GET /usage`. Quotas follow Appendix C (free: 300 summaries, 300 tag suggestions, 500 searches, 100 chat messages a day; Claude profile defaults too, all overridable with `QUOTA_*`). A call reserves its usage row under a per-user, per-operation lock before it runs, so parallel requests cannot pass the limit, and a failed call (including not enough content) gives its reservation back. Over the limit: `429 ai_quota_exceeded` with `reset_at` and `Retry-After`. In the Core API: `POST/DELETE /resources/{id}/summary`, `POST /ai/tag-suggestions`, `GET /ai/status` (cached 10 s), `GET /me/ai-usage`. AI failures map to `503 ai_unavailable` (Retry-After 30), `422 not_enough_content`, `429 ai_quota_exceeded`; tag suggestions instead return an empty list with `ai_available: false`. Automatic summary after a save (FR-AI-03) runs in the index job when the user's switch is on and the summary is empty or extracted; it never replaces a user's summary, writes without touching `updated_at`, and treats a refusal as "no summary" rather than a failed job. Two resilience rules were tightened: calls that spend quota (POST) are never retried automatically, and a 429 or 4xx answer neither retries nor counts toward the circuit breaker, so one user's quota cannot cut AI off for everyone.
- **5.6:** hybrid retrieval in the AI service (`app/retrieval/hybrid.py`): a vector leg (top 50 chunks by cosine distance, using the model's partial HNSW index) and a keyword leg (top 50 by `ts_rank_cd`, query words joined with OR so a question still matches), merged by reciprocal rank fusion (`1 / (60 + rank)`), then grouped to one hit per resource with the best chunk as the snippet. The snippet starts at the sentence holding the first query word, and drops the title line. Every query is filtered by the caller and by the embedding model. `POST /search` counts against the search quota and gives the count back if it fails; `GET /resources/{id}/related` uses the mean of the resource's chunk embeddings. In the Core API: `GET /search/semantic` (hits for deleted, archived or foreign resources are dropped; falls back to keyword search with `fallback: true` when the AI service is down, but a quota refusal is passed on) and `GET /resources/{id}/related` (up to 5; 503 when the AI service is down). A test checks that PostgreSQL can use the HNSW index for the exact query the search runs. Chunks no longer repeat the title in their text, since it is already added as a prefix.
- **5.7:** chat. AI service: `OllamaProvider` (streams `/api/chat`, `num_ctx` 8192, 15 s to the first piece and 120 s overall, `available()` checks the model is pulled), prompt templates (fixed system prompt; sources as numbered `<source>` blocks inside the user turn; last 6 messages of history), and sessions (`/chat/sessions*`) with the stored history, titled from the first question. Retrieval takes the best chunks (up to 8 and about 3,000 tokens), and keeps a chunk only if a query word matched it or its meaning is close enough (`CHAT_MIN_SIMILARITY`, default 0.5); with nothing relevant it does not call the model and says so, with no citations. Guardrails: page text cannot close or open a `<source>` block (the tags are broken with a zero-width character), and a streaming `MarkerFilter` drops any `[n]` that matches no retrieved source, rewrites `[1, 2]` as `[1][2]`, and leaves `a[0]` alone. Quota is reserved before the stream starts (so over-limit is a normal 429), and a stream that does not complete gives it back and saves nothing. Events are `token`, `citations`, `done` or `error`. Core API: `/chat/sessions*` and `POST /chat/sessions/{id}/messages`, which checks the scope collection is the user's, throws problems before the stream starts as normal error responses, then relays the events with `Cache-Control: no-cache`, `X-Accel-Buffering: no` and a comment line every 15 s, replacing each citation's title and address with the user's current ones and removing any that is not their live resource. `/ai/status` now reports `chat_available` from the model check.
- **5.8:** the evaluation harness is in `services/ai-service/tests/eval/` (see its README): 30 resources, 20 questions (10 answerable, 4 paraphrases, 6 unanswerable), 5 hostile pages, pure scoring functions, and a runner that indexes the library under a throwaway user, asks everything through the real chat service, times the first token, writes a JSON report, a Markdown summary and a CSV for hand scoring, and removes its data. The data, scoring and runner are tested with stand-in models in CI. **The evaluation itself has not been run**: it needs Ollama and the embedding model, which this environment cannot run, so ADR-011 and the defaults (`llama3.2:3b`, bge-small, `CHAT_MIN_SIMILARITY` 0.5) are still the planned values, not measured ones. `FastEmbedEmbedder` can now take an explicit dimension so a candidate embedding model can be compared without becoming a supported one.
- **5.10 (web):**
  - **Summary:** Summarise and Regenerate in the drawer. The label follows the source ("✦ AI-generated · may contain mistakes", "Extracted from the page", "Written by you"). A page with too little text says so, a summary the user wrote is only replaced after asking, and the buttons are disabled with the reason while the AI service is down.
  - **Tag suggestions:** "✦ Suggested tags" in quick-add (for a pasted link) and in the drawer. They are offered only; clicking adds a normal tag, and Accept all appears for two or more. A quiet note replaces them when the service is down, and nothing is requested when the user turned them off.
  - **Related:** a section in the drawer, left out when empty or unavailable.
  - **Semantic search:** the Keyword | Semantic switch (mode kept in the URL). A search by meaning runs when the user presses Enter, because each one counts against a daily limit. It shows the matching passage with the ✦ mark, hides filters and sorting, shows a banner when it fell back to keyword results, and is disabled when the service is down. "Search by meaning instead" is offered when a keyword search finds nothing.
  - **Ask your library** (`/ask`, `/ask/:id`): answers stream with Stop; citation markers become buttons that open the resource drawer; sources are listed under the finished answer; copy and retry; "Search the library instead" when nothing was cited; scope chosen when starting a chat; suggested questions from the user's tags and collections; past chats with rename and delete (behind a Chats button on a phone); states for an empty library, the daily limit (with the reset time, and "N questions left today" from 80 %), the service being down, and an interrupted answer that keeps its partial text. Ask is in the sidebar and the phone tab bar.
  - **Settings → AI:** the two switches, today's use against each limit, and what the mode means.
  - **Tests:** 58 new component tests against a fake AI service, and browser tests: the unavailable states against the default stack, and `E2E_AI=1` tests against a stack with the real AI service. The browser tests found three real defects, now fixed: loading placeholders with an `aria-label` and no role (also in the Stage 4 preview card), and suggested-question buttons that did not wrap on a phone.
  - **Not covered by a browser test:** a streamed answer end to end, because that needs the language model (the streaming and its failure modes are covered by component tests against a fake stream, and by the Core API and AI service tests).
- **5.9:** the Claude profile (`AI_PROFILE=claude`, FR-AI-01), switched on by configuration only.
  - **Providers:** `ClaudeSummarizer` and `ClaudeTagger` (a small model; the tagger is told the user's existing tags and they come first), `ClaudeProvider` (chat, streamed, no tools) and `VoyageEmbedder` (`voyage-4-lite`, 1,024 dimensions, batches of 128, vectors checked for size). They call the HTTP APIs directly with httpx rather than the vendors' SDKs, so they are tested with a mock transport and the service has two fewer dependencies. Page text goes to the model inside a block it is told is data, and the page cannot close the block. A thin page is refused before any paid call.
  - **Start-up checks:** the service refuses to start in the Claude profile without both keys, or without a price in `PRICE_TABLE` for every model in use. Prices are never guessed, so they are not defaulted either.
  - **Cost:** every call records its provider, model, tokens and estimated cost (`cost_usd` widened to 8 decimals in migration 0003, as embedding calls cost fractions of a cent). Providers that report tokens are trusted; others are estimated from the word count.
  - **Caps:** a per-user daily cost cap (default 0.50 USD) is checked with the count quotas and answers 429 `ai_quota_exceeded` with the reset time. A deployment-wide monthly cap (default 10 USD) switches summaries, tag suggestions and chat to the free providers, with a warning in the log. Embeddings never switch, as the index would no longer match.
  - **Re-embedding:** when the embedding model changes, a background task re-embeds each resource's stored chunks (their text already carries the title, so nothing is sent again from the Core API), then removes the old chunks, a few resources at a time. Until a resource is done, search does not find it. The design's "search keeps using the old model until the user's re-index finishes" is not done: it would need the old embedder to embed queries, and after a switch it may not be available. If the new model is down, nothing is lost and it resumes later.
  - **AI preferences:** the two switches (FR-AI-12) were built in 5.10; nothing else was needed here.
  - **Not tried:** the real Anthropic and Voyage APIs (no keys here). The request and response shapes follow their documented formats and are tested against mocks of them, so the first run with real keys is the real test; the streaming event handling and the 529 overload response are the parts most worth watching.
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

**Demo:** the home screen shows the library in numbers and a checklist that ticks itself; Settings → Data downloads the library as JSON and CSV; an admin finds a user, disables them, and they are out at once.

**Status:** 6.1 to 6.3 done.
- **6.1:** `GET /dashboard?time_zone=` (totals, by type, 5 newest, 12 Monday-start weeks in the user's zone with empty weeks filled, onboarding steps). Counts match the default Library list (deleted and archived left out). "Try a search" and "Ask a question" cannot be read off the data, so two flags on the user are set on first use (migration `Insights`); the first-resource and first-collection steps are worked out from the data, and the first stays ticked after that resource is deleted. Tests: `DashboardTests`.
- **6.2:** `GET /export?format=json|csv`, streamed in keyset-paged batches of 500 so memory does not grow with the library. JSON has `schema_version` 1, collections, resources and tags (archived included, deleted not). CSV has a byte order mark, RFC 4180 quoting, CRLF, tags joined with `;`, and a leading apostrophe on any cell that starts with `= + - @ TAB CR` so a spreadsheet does not run it as a formula (OWASP CSV injection). The JSON file is exact; only the CSV is altered. Responses are `attachment` and `no-store`; each export is audited as `data.exported` without its content; 6 a minute per user. Web: Settings → Data, and the delete-account page offers the same files first. Tests: `ExportTests` (includes a library of more than two batches with many equal timestamps).
- **6.3:** `GET /admin/users` (email search, paging), `PATCH /admin/users/{id}` (disabling rotates the security stamp, revokes refresh tokens and drops the cached session, so it takes effect at once; enabling does not bring old sessions back; an admin cannot disable themselves; both audited, repeats change nothing), `GET /admin/ai-usage`. Usage lives in the AI service, so the Core API asks `GET /admin/usage` there with a system token of scope `usage:read_all` (503 when it is down). Members get 403 on every admin endpoint. Web: Admin link and screens only for admins; a member who opens the URL is told they have no access and nothing is requested. Tests: `AdminTests`, `AiClientTests`, `tests/test_usage_api.py`, `admin.test.tsx`.
- **End to end** (`e2e/insights.spec.ts`, with axe checks): empty home then data and ticked steps, search step and dismissing, JSON and CSV downloads, member has no Admin link, admin disables and re-enables a user, own row has no switch, AI usage says when the AI service is off. The API must be started with `Auth__AdminEmail=e2e-admin@example.com` (or set `E2E_ADMIN_EMAIL`).
- Not done: the AI usage chart has not been seen with the real AI service running (the Core API call is tested against the real `AiClient` over HTTP and the AI endpoint against a real database, not against each other). The per-user view lists the first 100 users only.

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

**Status:** 7.1 to 7.7 done as far as research and files go; 7.1 to 7.5 checked as far as they can be without Docker or cloud accounts. 7.6 is written and never run.
- **7.1:** `contracts/run-schemathesis.sh` fuzzes every operation against the running API using `contracts/schemathesis.toml`; the `schemathesis` job in `contract.yml` builds the API against PostgreSQL and runs it (written, not run on GitHub: no Actions here). Six consecutive local runs end clean. `ContractDriftTests` compares the API's route table with the contract in both directions, with an explicit list of the Could operations not built (saved views, OAuth). This replaces the `oasdiff` comparison of a generated document: the Core API does not generate one, and an exact diff against a hand-written contract would be brittle. Fuzzing found real faults, all fixed with tests: a link with an impossible host (500), a date filter with a non-UTC offset (500), a NUL character in text (500 on save), an unmatched route or malformed ID (empty 404 instead of a problem document), and page numbers large enough to overflow. The contract was tightened or loosened where it disagreed with the API (URL format, tag and color values, nullable create fields, search limits). The `schemathesis.toml` that was there before used a key this version does not know, so it had never run.
- **7.2:** `e2e/smoke.spec.ts` signs out and in, adds a link, finds it by search and opens Ask, with accessibility checks. Full browser run on the default stack: 47 passed earlier in Stage 6, this one added.
- **7.3:** the API now sends the Doc 02 §9.5 headers (a stricter CSP than the web app's, since the API serves no pages) with tests. Cookie attributes and the CSRF check were reviewed against §9.1 and §9.3 and already match; `AuthTests` pins the attributes. Dependency audits (`dotnet list package --vulnerable`, `npm audit --omit=dev`, `pip-audit`) are clean for what ships and run in `security.yml`; `npm audit` also reports a moderate `minimatch` issue in the lint toolchain only. One note from the review: `GET /export` and the search milestone write on a GET. Both are harmless (an audit row, a one-way flag) and not exploitable cross-site, since a cross-site page cannot read the response.
- **7.4:** both services write one JSON object per log line with `trace_id` and `user_id`, and pass messages and exceptions through a redaction filter with unit tests (tokens, cookies, passwords and content fields removed, emails masked). A test logs a sign-in, a failed sign-in, a save with notes and a password reset at Trace level and checks nothing sensitive appears. The Core API exports traces and metrics over OTLP when `OTEL_EXPORTER_OTLP_ENDPOINT` is set (checked against a local receiver); logs deliberately are not exported that way, so nothing skips the filter. Measurements: the web server's request duration, outbox pending and dead, job duration, metadata outcomes, AI calls and duration by operation and outcome. The AI service takes the caller's `traceparent` into its logs. Not done: spans inside the AI service, and AI tokens and cost as metrics (they are in the usage table and the admin report); "always keep traces with errors" needs a collector, since the SDK samples at the start.
- **7.5:** the initial JavaScript is 146 KB gzipped against the 250 KB budget (the check already ran in `web.yml`). `services/core-api/perf/load.py` fills a library of 10,000 resources and measures nine scenarios from eight concurrent clients: p95 is under 80 ms everywhere (targets 300 ms and 500 ms). That is one machine with no network between the parts, so it shows the queries and indexes are sound, not what a deployment will do. Accessibility is checked with axe on every screen in the browser tests. Not measured: LCP with Lighthouse, semantic search and chat latency (they need the AI models).

## Cross-cutting checks

- **Every stage:** update the traceability matrix in Doc 01 when a requirement is implemented; add tests named in the matrix.
- **Risks to test early:** SSE through Vercel (AR-03) in Stage 5, Oracle idle reclaim and ARM image builds (AR-01, OI-08) before Stage 7.
- **Order flexibility:** the web screens for a stage can lag the API by one stage. Stages 6 and 4 are independent of each other and can swap.
- **7.6:** `infra/deploy/` holds the production `compose.yaml` (only Caddy publishes ports; migrations run as one-shot jobs first), the `Caddyfile` for the Vercel layout, a same-origin `Caddyfile.same-origin` with `web.Dockerfile` as the AR-03 fallback, `.env.example` and a runbook with a first-deployment checklist. `deploy.yml` builds arm64 images to GHCR and rolls out over SSH; it does nothing until `DEPLOY_ENABLED` is `true`. Checked: the YAML parses, the Core API healthcheck command works against the real API (up and down), and the AI service accepts the empty defaults the compose file passes. Not checked: any Docker build, the Caddyfile (no Caddy), the workflow, or anything on a real VM. Reading the design turned up one real defect, now fixed with tests: behind a proxy the API saw the proxy's address for every request, which would have put all users under one rate limit and one audit address. `Proxy:TrustForwardedHeaders` (on in the compose file) makes it believe the last hop's `X-Forwarded-For` and `X-Forwarded-Proto`. A known open point, listed in the runbook: behind the Vercel rewrite the "client" is Vercel, so the real visitor address may still not arrive; the same-origin fallback solves it.
- **7.7:** terms re-read on 2026-11-03 (sources and caveats in [ADR-012](adr/012-demo-hosting-after-recheck.md)). Findings that changed the plan: Vercel cuts proxied requests at 30 seconds, but chat answers can take 120, so **the web app is now served by Caddy on the VM** (same origin); Neon Free gives 100 compute-hours a month and suspends only after 5 idle minutes, while the outbox polls every 2 seconds, so **PostgreSQL runs on the VM** (compose profile `local-db`, `make-init.sh` for passwords, `backup.sh` nightly), with Neon left as an option; Oracle's allowance is reported as 2 OCPU / 12 GB, so the plan is sized to that. **Email: Brevo's SMTP relay** (300 a day free), second choice Mailjet. AR-03 is thereby resolved against the Vercel rewrite; OI-04 is chosen; OI-08 (whether Pay As You Go removes idle reclaim) is still open and the plan does not depend on it. The figures come from search results and the providers' pages as far as they could be reached, and are to be re-read on deployment day (checklist in `infra/deploy/README.md`). Not done: reading Oracle's live terms, creating any account, or trying any of it.
