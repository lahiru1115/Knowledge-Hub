# Demo deployment

One VM runs Caddy, the Core API, the AI service and (for the free AI profile) Ollama. The database is Neon. The web app
is on Vercel, or served by Caddy itself. Doc 02 §10.2 explains the choices and the fallback.

**Status:** written and checked for syntax, never run. There is no Docker or cloud account where this was prepared, so
the first deployment will find things. The list under "Check on first deployment" says what to look at.

## What is here

| File | What it does |
|---|---|
| `compose.yaml` | The services. Only Caddy publishes ports; the API, AI service and Ollama are on an internal network. Two one-shot jobs (`migrate-core`, `migrate-ai`) run before the services that need them. |
| `Caddyfile` | TLS and `/api/*` to the Core API, response streaming on, `/health/ready` hidden. Use with the web app on Vercel. |
| `Caddyfile.same-origin`, `web.Dockerfile` | Fallback: Caddy serves the web app too, so there is no Vercel rewrite (AR-03) and the real client address reaches the API. |
| `.env.example` | Every setting, with notes. |
| `../../apps/web/vercel.json` | The rewrite of `/api/*` to the VM, and the security headers for the web app. |
| `../../.github/workflows/deploy.yml` | Builds arm64 images, pushes them to GHCR, updates the VM. Does nothing until `DEPLOY_ENABLED` is `true`. |

## One-time setup

1. **Database (Neon).** Create a project in the region nearest the VM and a database `knowledge_hub`. As the owner, run
   `infra/db/init.sql` once, after changing the four role passwords in it. Put the pooled host in the `*_APP_*` settings and
   the direct host in the `*_MIGRATOR_*` ones (migrations should not go through the pooler). Check
   `infra/db/check-roles.sh` against it.
2. **VM.** Install Docker and the compose plugin. Open ports 80, 443 and 22 only. Point a DNS name (`API_HOST`) at it.
   Create `~/knowledge-hub/.env` from `.env.example`, with fresh secrets. Log in to GHCR so it can pull
   (`docker login ghcr.io` with a token that can read packages).
3. **GitHub.** Repository variable `DEPLOY_ENABLED=true`. Secrets: `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY`,
   `DEPLOY_API_HOST` (the same name as `API_HOST`). An environment called `demo` (add required reviewers if you want to
   approve each rollout).
4. **Web.** In Vercel, import the repository with root `apps/web`. Put the real API host into `apps/web/vercel.json`
   (`REPLACE-WITH-API-HOST`) and commit it. Set `Auth__AppBaseUrl` (`APP_BASE_URL`) to the Vercel address.
5. **First start.** Push to `main` (or run the workflow). For the free AI profile start with the `free-ai` profile and pull
   the chat model once: `docker compose --env-file .env --profile free-ai up -d`, then
   `docker compose exec ollama ollama pull llama3.2:3b` (or the model in `LLM_MODEL`). The embedding model downloads on
   the AI service's first use and is kept in the `models` volume.
6. **Administrator.** Register in the web app with the address in `ADMIN_EMAIL`. The first account with that address
   becomes the administrator.

## Everyday

```bash
docker compose --env-file .env ps
docker compose --env-file .env logs -f core-api        # one JSON object per line; filter on trace_id
docker compose --env-file .env logs ai-service | grep '"level": "error"'
```

**Roll back:** set `TAG=` in `.env` to an earlier 12-character commit tag still in GHCR, then
`docker compose --env-file .env up -d`. Migrations only move forward; a release that changed the schema needs a fix-forward
or a restore from Neon's history.

**Backups:** Neon keeps point-in-time history on its free plan for a short window only. Take your own `pg_dump` before a risky
change. The only state on the VM is certificates (`caddy-data`) and downloaded models, both rebuildable.

## Check on first deployment

These are the open questions in the architecture document. Each can only be answered on the real host.

- [ ] **Free-tier terms (AR-01, OI-08).** Re-read Oracle's Always Free allowance and Neon's storage and compute limits today.
      Confirm whether converting the Oracle account to Pay As You Go removes idle reclaim before relying on the VM.
- [ ] **Chat through the Vercel rewrite (AR-03).** Sign in on the Vercel address, ask a question, and watch the answer arrive
      word by word rather than all at once, and with no cut-off on a long answer. If it is buffered or stops, switch to the
      same-origin fallback: build `web.Dockerfile`, set `CADDYFILE=./Caddyfile.same-origin` and `CADDY_IMAGE`, and point DNS
      for the app name at the VM.
- [ ] **Client addresses.** Behind Vercel the API sees Vercel's address in `X-Forwarded-For`, not the visitor's, so the per-IP
      limits on sign-in and registration would be shared by everyone. Check the audit log (`core.audit_events.ip`) after two
      sign-ins from different networks. If they match, use the same-origin fallback, where Caddy sees the real address.
- [ ] **Email (OI-04).** Choose a provider with SMTP, fill in `SMTP_*`, and request a password reset to your own address.
- [ ] **Speed of chat on the VM (AR-02).** Time a first token. On 2 OCPU it will be slower than the reference laptop; consider
      a smaller model or `AI_PROFILE=claude` with the cost caps on.
- [ ] **The arm64 build.** The first build under emulation is slow (the .NET publish especially); later ones use the cache.
- [ ] **pgvector.** Neon offers a recent pgvector, but check it is 0.8 or newer (`SELECT extversion FROM pg_extension WHERE extname='vector'`):
      semantic search relies on `hnsw.iterative_scan`.
- [ ] **Ports.** From another machine, `nmap -p- API_HOST` shows only 80 and 443 (and 22). Port 8000 and 8080 are closed.
