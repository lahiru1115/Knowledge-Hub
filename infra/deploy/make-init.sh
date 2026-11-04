#!/usr/bin/env bash
# Makes init.local.sql from init.sql (next to this script on the VM, or ../db/init.sql in a checkout) with fresh passwords for the four database roles, and prints the lines to put
# in .env. Run once, before the first start of the `postgres` service (the file only runs when the database is created).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
[ -e "$here/init.local.sql" ] && { echo "init.local.sql already exists; delete it to make a new one." >&2; exit 1; }
src=""
for candidate in "$here/init.sql" "$here/../db/init.sql"; do [ -f "$candidate" ] && { src="$candidate"; break; }; done
[ -n "$src" ] || { echo "init.sql not found next to this script or in ../db. Copy infra/db/init.sql here." >&2; exit 1; }

pw() { openssl rand -hex 24; }
core_migrator="$(pw)"; core_app="$(pw)"; ai_migrator="$(pw)"; ai_app="$(pw)"; super="$(pw)"

# Written to a temporary file first, so a failure never leaves a half-made init.local.sql behind: an empty one would
# start PostgreSQL without any of the roles.
tmp="$(mktemp "$here/.init.local.XXXXXX")"
trap 'rm -f "$tmp"' EXIT
sed -e "s/core_migrator_dev/$core_migrator/" -e "s/core_app_dev/$core_app/" \
    -e "s/ai_migrator_dev/$ai_migrator/" -e "s/ai_app_dev/$ai_app/" "$src" > "$tmp"
if grep -q '_dev' "$tmp"; then echo "A development password is still in the result." >&2; exit 1; fi
[ "$(grep -c 'CREATE ROLE' "$tmp")" -eq 4 ] || { echo "Expected four CREATE ROLE lines in $src." >&2; exit 1; }
chmod 600 "$tmp"
mv "$tmp" "$here/init.local.sql"
trap - EXIT

cat <<ENV
# Add to .env (the database is on the internal network only; its host name is "postgres"):
COMPOSE_PROFILES=local-db
POSTGRES_SUPERUSER_PASSWORD=$super
CORE_APP_CONNECTION=Host=postgres;Database=knowledge_hub;Username=core_app;Password=$core_app
CORE_MIGRATOR_CONNECTION=Host=postgres;Database=knowledge_hub;Username=core_migrator;Password=$core_migrator
AI_APP_DATABASE_URL=postgresql://ai_app:$ai_app@postgres/knowledge_hub
AI_MIGRATOR_DATABASE_URL=postgresql://ai_migrator:$ai_migrator@postgres/knowledge_hub
ENV
