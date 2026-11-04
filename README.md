# Knowledge Hub

A personal knowledge library: save links, organise them in collections and tags, find them again with keyword and
semantic search, and ask questions answered from your own library with citations. AI summaries and tag suggestions are
optional and run free by default (local models), with a paid Claude profile that has cost caps.

The design is in `docs/`: requirements ([01](docs/01-software-requirements-specification.md)), architecture
([02](docs/02-system-architecture.md)), UI/UX ([03](docs/03-ui-ux-design-plan.md)), the build order with the status of
every stage ([04](docs/04-build-order.md)), and the decision records in [`docs/adr/`](docs/adr/README.md).

## What is where

| Path | Contents |
|---|---|
| `contracts/openapi.yaml` | The API contract, written before each endpoint. The web client is generated from it; CI fuzzes the API against it. |
| `apps/web/` | React 19 + Vite + TypeScript single-page app (TanStack Router and Query, shadcn/ui, Tailwind). |
| `services/core-api/` | .NET 10 Core API: identity, library, search, export, admin, outbox worker. PostgreSQL schema `core`. |
| `services/ai-service/` | FastAPI AI service: indexing, hybrid search, summaries, tag suggestions, chat. PostgreSQL schema `ai` with pgvector. |
| `infra/db/` | `init.sql` (roles and schemas) and a check that the roles stay apart. |
| `infra/deploy/` | Demo deployment on one VM: compose file, Caddy, env template, runbook. |
| `.github/workflows/` | CI for each part, contract fuzzing, security audits, deployment. |

## Run it locally

With Docker, from the repository root:

```bash
cp .env.example .env     # optional: every setting has a development default
docker compose up
```

Open http://localhost:5173. The first start builds the images and downloads the chat model (about 2 GB), so give it a
few minutes; chat answers appear once the download ends, everything else works meanwhile. Register an account, and
register `admin@example.com` (or your `ADMIN_EMAIL`) to get the administrator. Password-reset emails land in Mailpit at
http://localhost:8025, traces in Jaeger at http://localhost:16686, the API on :8080 and the AI service on :8000.

The web container runs the Vite dev server on your files, so edits to `apps/web/src` reload at once. The two .NET and
Python services are built into images: rebuild one with `docker compose up --build core-api`.

Without Docker you need PostgreSQL 16+ with pgvector (`infra/db/init.sql` applied), the .NET 10 SDK, Node 22 and `uv`:

**Core API** (http://localhost:8080):

```bash
cd services/core-api
ConnectionStrings__Core="Host=localhost;Database=knowledge_hub;Username=core_app;Password=core_app_dev" \
Auth__SigningKey="a-local-signing-key-of-at-least-32-bytes" Auth__SecureCookies=false \
ASPNETCORE_URLS=http://localhost:8080 dotnet run --project src/KnowledgeHub.Api
```

Create its tables first with `dotnet ef database update --project src/KnowledgeHub.Infrastructure --connection "Host=localhost;Database=knowledge_hub;Username=core_migrator;Password=core_migrator_dev"`.

**AI service** (optional; the app works without it and says so; http://localhost:8000):

```bash
cd services/ai-service
AI_MIGRATOR_DATABASE_URL=postgresql://ai_migrator:ai_migrator_dev@localhost:5432/knowledge_hub uv run alembic upgrade head
AI_DATABASE_URL=postgresql://ai_app:ai_app_dev@localhost:5432/knowledge_hub SERVICE_JWT_SECRET=<same as AiService__JwtSecret> \
  uv run uvicorn app.main:create_app --factory --port 8000
```

Then start the Core API with `AiService__Enabled=true` and `AiService__JwtSecret` set to the same secret. Chat needs
Ollama with a model (`ollama pull llama3.2:3b`); summaries and tag suggestions do not.

**Web app** (http://localhost:5173; it proxies `/api` to the Core API):

```bash
cd apps/web
npm ci
npm run dev
```

## Tests

| Part | Command | Needs |
|---|---|---|
| Core API | `dotnet test` in `services/core-api` | PostgreSQL: set `KH_TEST_MIGRATOR_CONNECTION` and `KH_TEST_APP_CONNECTION`, or let Testcontainers start one |
| AI service | `uv run pytest` in `services/ai-service` | PostgreSQL with the `ai` schema migrated (`AI_TEST_APP_URL`, `AI_TEST_MIGRATOR_URL`) |
| Web | `npm test` in `apps/web` | nothing |
| Browser | `npm run e2e` in `apps/web` | the running stack; see `apps/web/e2e/README.md` |
| Contract | `contracts/run-schemathesis.sh` | a running Core API with raised rate limits |
| Latency | `uv run --with httpx perf/load.py` in `services/core-api` | a running Core API; see `perf/README.md` |

## Status

All seven stages of the build order are done as code; [Doc 04](docs/04-build-order.md) has the detail. In short:

- **Verified here:** every test suite above, against a real PostgreSQL and the real API. That includes 48 browser tests
  with accessibility checks, contract fuzzing, and a latency check on 10,000 resources.
- **Not verified:** the real AI models (the embedding model and chat model never ran here), the paid Claude and
  Voyage APIs, Docker builds, the deployment, and anything on real hosting. The evaluation harness for search and chat
  quality exists but has not been run.
- **Not built (Could):** saved views and sign-in with OAuth. They are in the contract and listed as not built.
