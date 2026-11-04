#!/usr/bin/env bash
# Makes init.local.sql from ../db/init.sql with fresh passwords for the four database roles, and prints the lines to put
# in .env. Run once, before the first start of the `postgres` service (the file only runs when the database is created).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -e "$here/init.local.sql" ] && { echo "init.local.sql already exists; delete it to make a new one." >&2; exit 1; }
pw() { openssl rand -hex 24; }
core_migrator="$(pw)"; core_app="$(pw)"; ai_migrator="$(pw)"; ai_app="$(pw)"; super="$(pw)"

sed -e "s/core_migrator_dev/$core_migrator/" -e "s/core_app_dev/$core_app/" \
    -e "s/ai_migrator_dev/$ai_migrator/" -e "s/ai_app_dev/$ai_app/" "$here/../db/init.sql" > "$here/init.local.sql"
chmod 600 "$here/init.local.sql"
if grep -q '_dev' "$here/init.local.sql"; then echo "A development password is still in init.local.sql." >&2; exit 1; fi

cat <<ENV
# Add to .env (the database is on the internal network only; its host name is "postgres"):
COMPOSE_PROFILES=local-db
POSTGRES_SUPERUSER_PASSWORD=$super
CORE_APP_CONNECTION=Host=postgres;Database=knowledge_hub;Username=core_app;Password=$core_app
CORE_MIGRATOR_CONNECTION=Host=postgres;Database=knowledge_hub;Username=core_migrator;Password=$core_migrator
AI_APP_DATABASE_URL=postgresql://ai_app:$ai_app@postgres/knowledge_hub
AI_MIGRATOR_DATABASE_URL=postgresql://ai_migrator:$ai_migrator@postgres/knowledge_hub
ENV
