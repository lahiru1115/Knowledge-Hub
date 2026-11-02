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
