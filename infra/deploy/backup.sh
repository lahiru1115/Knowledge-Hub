#!/usr/bin/env bash
# Nightly dump of the local database, keeping the last 14. Add to cron on the VM, for example:
#   17 3 * * * cd ~/knowledge-hub && ./backup.sh >> backups/backup.log 2>&1
# Restore: gunzip -c backups/knowledge_hub-YYYYMMDD-HHMM.sql.gz | docker compose exec -T postgres psql -U postgres knowledge_hub
# A copy that stays on the same disk does not survive losing the VM: also copy ./backups somewhere else.
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")"
mkdir -p backups
out="backups/knowledge_hub-$(date +%Y%m%d-%H%M).sql.gz"
docker compose exec -T postgres pg_dump -U postgres --clean --if-exists knowledge_hub | gzip > "$out.part"
mv "$out.part" "$out"
ls -1t backups/knowledge_hub-*.sql.gz | tail -n +15 | xargs -r rm --
echo "$(date -Is) wrote $out ($(du -h "$out" | cut -f1))"
