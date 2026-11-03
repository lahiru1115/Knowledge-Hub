# ADR-012: Demo hosting after re-checking free-tier terms

| | |
|---|---|
| **Status** | Accepted |
| **Date** | 2026-11-03 |
| **Source** | Build order 7.7; changes parts of [ADR-010](010-single-vm-demo-hosting.md) |

## Context

AR-01, AR-03 and OI-04 asked for the free-tier terms to be re-read before deployment, and for an email provider to be
chosen. The terms below were read on 2026-11-03 from search results and the providers' own pages where they could be
reached. They are not guaranteed current: re-read them on the day of deployment.

| Service | What was found | What it means here |
|---|---|---|
| Oracle Always Free, Ampere A1 | Recent guides report the allowance cut from 4 OCPU / 24 GB to 2 OCPU / 12 GB (enforced from 18 August 2026). Oracle's own page, as indexed, still showed the older figures. An instance counts as idle if over 7 days CPU, network and (on A1) memory use are each under 20 % at the 95th percentile, and may be reclaimed. Sources disagree on whether Pay As You Go removes the limit or bills above it. | Plan for 2 OCPU / 12 GB. Do not rely on Pay As You Go to keep more. OI-08 is not settled: ask Oracle. |
| Neon Free | 100 compute-hours per project per month, 1 GB storage per project (an older page said 0.5 GB). Compute suspends after 5 minutes without activity and cannot be changed on Free. When the hours run out, compute stays suspended until the next period. | The outbox worker polls every 2 seconds, so the database never goes idle: at the smallest size (0.25 CU) that is about 180 compute-hours a month, so compute would be switched off in the second half of the month. |
| Vercel Hobby | Non-commercial personal use only. A proxied request (a rewrite to an external address) times out after at most 30 seconds. | Chat answers can take up to 120 seconds (`CHAT_TOTAL_SECONDS`), so they would be cut off through the rewrite. AR-03 is confirmed, not just possible. |
| Azure Container Apps (fallback) | Free each month per subscription: 180,000 vCPU-seconds, 360,000 GiB-seconds, 2 million requests. No change found. | Unchanged; not re-evaluated further. |
| Email | Brevo: 300 a day free, SMTP relay, branded footer. Mailjet: 200 a day and 6,000 a month. Resend: reported 100 a day and 3,000 a month. SendGrid's free plan was retired in 2025. | See the decision. |

## Decision

1. **Serve the web app from Caddy on the VM** (same origin, no Vercel rewrite). This removes the 30-second cut-off, gives the
   API the real client address, and keeps the cookies first-party. Vercel stays possible for the static app only if chat is
   left out, and is documented as an option, not the default.
2. **Run PostgreSQL on the VM** (`pgvector/pgvector` in a compose profile) instead of Neon Free, with a nightly `pg_dump`.
   Neon Free stays possible for a demo whose background workers are slowed to poll every few minutes, which delays
   metadata and indexing by that much.
3. **Email through Brevo's SMTP relay.** It fits the existing SMTP settings (STARTTLS on port 587, user name and key), and
   300 a day is far more than password-reset traffic needs. Mailjet is the second choice. The sending domain needs SPF and
   DKIM set up in the provider, or reset emails land in spam.

## Consequences

- The VM now holds state (the database), so the backup job and disk space matter; the VM is no longer disposable.
- One host name serves everything, so `APP_BASE_URL` and the site host are the same.
- The free-tier figures above will go stale; the deployment README lists them as a checklist to redo.
- Not done: reading Oracle's live terms, or confirming how Pay As You Go interacts with the new limit.

## Alternatives considered

- Keep Neon and lengthen the outbox poll to several minutes: cheap, but every save then waits that long for its metadata.
- Neon paid plan: removes the cap but is no longer free.
- Vercel Pro: lifts the commercial restriction but the proxy timeout would still need checking for the plan.
