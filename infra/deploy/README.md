# Demo deployment

One VM runs Caddy (which also serves the web app), the Core API, the AI service, PostgreSQL and, for the free AI
profile, Ollama. [ADR-012](../../docs/adr/012-demo-hosting-after-recheck.md) explains why this differs from the first
plan (Neon and Vercel) and lists the free-tier terms as they were read on 2026-11-03.

**Status:** written and checked for syntax, never run. There is no Docker or cloud account where this was prepared, so
the first deployment will find things. "Check on first deployment" says what to look at.

## What is here

| File | What it does |
|---|---|
| `compose.yaml` | The services. Only Caddy publishes ports. Migrations run as one-shot jobs first. PostgreSQL is in the `local-db` profile, Ollama in `free-ai`; both are switched on with `COMPOSE_PROFILES` in `.env`. |
| `Caddyfile.same-origin`, `web.Dockerfile` | The default: Caddy serves the web app and forwards `/api/*`, with response streaming on and `/health/ready` hidden. |
| `Caddyfile` | The alternative layout with the web app on Vercel. Not the default: Vercel cuts proxied requests at 30 seconds, which ends long chat answers. |
| `make-init.sh` | Makes `init.local.sql` with fresh role passwords and prints the `.env` lines for the local database. |
| `backup.sh` | Nightly `pg_dump`, keeps 14. |
| `.env.example` | Every setting, with notes. |
| `../../.github/workflows/deploy.yml` | Builds arm64 images to GHCR and updates the VM. Does nothing until `DEPLOY_ENABLED` is `true`. |

## One-time setup

1. **VM.** Ampere A1, **2 OCPU / 12 GB** (the reported current Always Free limit). Install Docker and the compose plugin.
   Open ports 80, 443 and 22 only. Point a DNS name (`SITE_HOST`) at it. Log in to GHCR so it can pull
   (`docker login ghcr.io` with a token that can read packages).
2. **Files and secrets.** On the VM, `mkdir -p ~/knowledge-hub`. From a checkout of this repository, copy the files the
   first start needs (the workflow keeps them up to date afterwards):

   ```bash
   scp infra/deploy/compose.yaml infra/deploy/Caddyfile.same-origin infra/deploy/make-init.sh \
       infra/deploy/backup.sh infra/deploy/.env.example infra/db/init.sql  USER@HOST:~/knowledge-hub/
   ```

   Then on the VM, in `~/knowledge-hub`: `cp .env.example .env`, fill in the secrets (`openssl rand -base64 48`), and run
   `./make-init.sh >> .env`. That sets the local database up with fresh passwords (it needs `init.sql` in the same
   folder, and refuses to leave a half-made file if anything goes wrong). Keep `.env` and `init.local.sql` out of git.
3. **Email.** Create a Brevo account, verify the sending domain (add its SPF and DKIM records in DNS), create an SMTP key,
   and fill in `SMTP_*` in `.env`.
4. **GitHub.** Repository variable `DEPLOY_ENABLED=true`. Secrets: `DEPLOY_HOST`, `DEPLOY_USER`, `DEPLOY_SSH_KEY`,
   `DEPLOY_SITE_HOST` (the same as `SITE_HOST`). An environment called `demo` (add required reviewers to approve each
   rollout).
5. **First start.** Push to `main`, or run the workflow. It builds the images (slow the first time: emulated arm64), starts
   PostgreSQL, runs both migrations, and starts the rest. For the free AI profile, pull the chat model once:
   `docker compose --env-file .env exec ollama ollama pull llama3.2:3b` (or the model in `LLM_MODEL`). The embedding model
   downloads on the AI service's first use and is kept in the `models` volume.
6. **Backups.** `crontab -e`: `17 3 * * * cd ~/knowledge-hub && ./backup.sh >> backups/backup.log 2>&1`, and copy
   `backups/` off the VM now and then (a backup on the same disk does not survive losing the VM).
7. **Administrator.** Register in the web app with the address in `ADMIN_EMAIL`; it becomes the administrator.

To use Neon instead of the local database, follow the comments in `.env.example`, run `infra/db/init.sql` there once as
the owner, and drop `local-db` from `COMPOSE_PROFILES`.

## Everyday

```bash
docker compose --env-file .env ps
docker compose --env-file .env logs -f core-api        # one JSON object per line; filter on trace_id
docker compose --env-file .env logs ai-service | grep '"level": "error"'
```

**Roll back:** set `TAG=` in `.env` to an earlier 12-character commit tag still in GHCR, then
`docker compose --env-file .env up -d`. Migrations only move forward; a release that changed the schema needs a fix-forward
or a restore from `backups/`.

## Check on first deployment

Terms and limits change. Redo the first four on the day, from the providers' own pages.

- [ ] **Oracle Always Free (AR-01, OI-08).** Confirm the Ampere A1 allowance (reported 2 OCPU / 12 GB since 18 August
      2026) and the idle-reclaim rule (reclaimed if over 7 days CPU, network and memory are each under 20 % at the 95th
      percentile). Ask Oracle whether Pay As You Go removes reclaim or bills above the limit: sources disagree, and the
      plan does not depend on it.
- [ ] **Brevo (OI-04).** Confirm the free plan still allows 300 emails a day over SMTP. Request a password reset to your own
      address and check it is not in spam (SPF and DKIM).
- [ ] **Neon, only if used.** Compute hours per month and storage per project, and that a worker polling every 2 seconds
      still exhausts them.
- [ ] **Vercel, only if used.** The proxy time limit, and that the use counts as non-commercial.
- [ ] **Chat.** Ask a long question in the web app and watch the answer arrive word by word until the end, with no cut-off.
- [ ] **Client addresses.** Sign in from two different networks and check `core.audit_events.ip` shows two different
      addresses, not Caddy's.
- [ ] **Speed of chat on the VM (AR-02).** Time a first token. On 2 OCPU it will be slower than the reference laptop;
      consider a smaller model or `AI_PROFILE=claude` with the cost caps on.
- [ ] **pgvector.** `SELECT extversion FROM pg_extension WHERE extname='vector'` is 0.8 or newer (semantic search relies on
      `hnsw.iterative_scan`). The `pgvector/pgvector:pg17` image should give it; check.
- [ ] **Ports.** From another machine, `nmap -p- SITE_HOST` shows only 80 and 443 (and 22).
- [ ] **A restore.** Run `backup.sh`, then restore the file into a scratch database. An untested backup is a guess.
