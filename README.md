# Knowledge Hub

Personal knowledge library with AI-assisted summaries, tagging, semantic search and "Ask your library".

| Path | Contents |
|---|---|
| `docs/` | SRS (01), architecture (02), UI/UX plan (03); `docs/adr/` decision records |
| `contracts/` | `openapi.yaml`, the API source of truth (write it before any endpoint) |
| `apps/web/` | React + Vite SPA |
| `services/core-api/` | .NET 10 Core API |
| `services/ai-service/` | FastAPI AI service |
| `infra/` | Database init script, demo deployment files |
| `plans/` | Planning documents |

Local infrastructure: `cp .env.example .env && docker compose up`. Application services are added as they are built.

## Web app

```bash
cd apps/web
npm ci
npm run dev        # http://localhost:5173, proxies /api to the Core API on :8080
npm test           # unit and component tests
npm run e2e        # browser tests; see apps/web/e2e/README.md
```
