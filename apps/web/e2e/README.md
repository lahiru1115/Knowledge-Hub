# Browser tests

These run in a real browser against the real stack. They are not part of `web.yml` yet; the compose-based
CI job arrives with Stage 7 (Doc 02 §10.3).

1. Start PostgreSQL with `infra/db/init.sql` applied and the migrations run (`docker compose up -d postgres
migrate-core`).
2. Start the Core API on port 8080 with the settings in `.env.example` (`ConnectionStrings__Core`,
   `Auth__SigningKey`). Raise the sign-in and registration rate limits for test runs, for example
   `Auth__RateLimits__RegisterPerIpPerHour=1000` and `Auth__RateLimits__LoginPerIpPerMinute=1000`.
3. Do not run Mailpit on the same ports: the tests start a small mail server of their own (SMTP :1025, HTTP :8025)
   so they can read password reset emails.
4. `npm run e2e` from `apps/web` (Playwright starts the Vite dev server).
5. Leave the outbox worker on (the default). Two tests rely on it: one saves a link to a private address and
   expects the server to refuse it, the other saves an unreachable host. Neither needs the internet.
6. The AI states: `ai-unavailable.spec.ts` runs against the stack above, where the AI service is off. `ai.spec.ts` needs
   the AI service too and is skipped unless `E2E_AI=1`. Start the AI service (`uv run uvicorn app.main:create_app
--factory --port 8000` in `services/ai-service`, after `alembic upgrade head`), and the Core API with
   `AiService__Enabled=true`, `AiService__JwtSecret` equal to the AI service's `SERVICE_JWT_SECRET`, and
   `Outbox__Enabled=false` (without the embedding model, background indexing would fail and open the circuit
   breaker). Summaries and tag suggestions need no model; semantic search and Ask show their unavailable behaviour
   unless the models are installed.
