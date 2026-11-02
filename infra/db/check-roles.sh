#!/usr/bin/env bash
# Verifies the database role isolation described in Doc 02 §7.1 (ADR-008):
#   core_app may use `core` and nothing in `ai`; ai_app the reverse; app roles cannot run DDL.
# Run after `docker compose up -d postgres` (or against any database prepared with infra/db/init.sql).
# Connection settings (defaults match .env.example):
#   PGHOST=localhost PGPORT=5432 PGDATABASE=knowledge_hub
#   CORE_MIGRATOR_PASSWORD, CORE_APP_PASSWORD, AI_MIGRATOR_PASSWORD, AI_APP_PASSWORD
set -uo pipefail

export PGHOST="${PGHOST:-localhost}" PGPORT="${PGPORT:-5432}" PGDATABASE="${PGDATABASE:-knowledge_hub}"
CORE_MIGRATOR_PASSWORD="${CORE_MIGRATOR_PASSWORD:-core_migrator_dev}"
CORE_APP_PASSWORD="${CORE_APP_PASSWORD:-core_app_dev}"
AI_MIGRATOR_PASSWORD="${AI_MIGRATOR_PASSWORD:-ai_migrator_dev}"
AI_APP_PASSWORD="${AI_APP_PASSWORD:-ai_app_dev}"

pass=0; fail=0

# as <role> <password> <sql> : prints nothing, returns psql's exit status
as() { PGPASSWORD="$2" psql -X -q -v ON_ERROR_STOP=1 -U "$1" -c "$3" >/dev/null 2>&1; }

expect_ok()   { local d="$1"; shift; if as "$@"; then ok "$d"; else bad "$d (was denied)"; fi; }
expect_deny() { local d="$1"; shift; if as "$@"; then bad "$d (was allowed)"; else ok "$d"; fi; }
ok()  { echo "  ok    $1"; pass=$((pass + 1)); }
bad() { echo "  FAIL  $1"; fail=$((fail + 1)); }

cleanup() {
  as core_migrator "$CORE_MIGRATOR_PASSWORD" "DROP TABLE IF EXISTS core._role_check"
  as ai_migrator   "$AI_MIGRATOR_PASSWORD"   "DROP TABLE IF EXISTS ai._role_check"
}
trap cleanup EXIT

echo "Connecting to $PGHOST:$PGPORT/$PGDATABASE"
if ! as core_migrator "$CORE_MIGRATOR_PASSWORD" "SELECT 1"; then
  echo "Cannot connect as core_migrator. Is the database up and initialised with infra/db/init.sql?" >&2
  exit 2
fi

# Fixture tables, created the way the real migrations will create tables (by the migrator roles).
as core_migrator "$CORE_MIGRATOR_PASSWORD" "CREATE TABLE core._role_check (id int)" || { echo "cannot create core fixture" >&2; exit 2; }
as ai_migrator   "$AI_MIGRATOR_PASSWORD"   "CREATE TABLE ai._role_check (id int)"   || { echo "cannot create ai fixture" >&2; exit 2; }

echo "core_app"
expect_ok   "can insert, select, update, delete in core"  core_app "$CORE_APP_PASSWORD" \
  "INSERT INTO core._role_check VALUES (1); SELECT * FROM core._role_check; UPDATE core._role_check SET id = 2; DELETE FROM core._role_check"
expect_deny "cannot select from ai"                       core_app "$CORE_APP_PASSWORD" "SELECT * FROM ai._role_check"
expect_deny "cannot insert into ai"                       core_app "$CORE_APP_PASSWORD" "INSERT INTO ai._role_check VALUES (1)"
expect_deny "cannot create tables in core"                core_app "$CORE_APP_PASSWORD" "CREATE TABLE core._ddl_attempt (id int)"
expect_deny "cannot create tables in ai"                  core_app "$CORE_APP_PASSWORD" "CREATE TABLE ai._ddl_attempt (id int)"
expect_deny "cannot create tables in public"              core_app "$CORE_APP_PASSWORD" "CREATE TABLE public._ddl_attempt (id int)"
expect_deny "cannot drop core tables"                     core_app "$CORE_APP_PASSWORD" "DROP TABLE core._role_check"

echo "ai_app"
expect_ok   "can insert, select, update, delete in ai"    ai_app "$AI_APP_PASSWORD" \
  "INSERT INTO ai._role_check VALUES (1); SELECT * FROM ai._role_check; UPDATE ai._role_check SET id = 2; DELETE FROM ai._role_check"
expect_deny "cannot select from core"                     ai_app "$AI_APP_PASSWORD" "SELECT * FROM core._role_check"
expect_deny "cannot insert into core"                     ai_app "$AI_APP_PASSWORD" "INSERT INTO core._role_check VALUES (1)"
expect_deny "cannot create tables in ai"                  ai_app "$AI_APP_PASSWORD" "CREATE TABLE ai._ddl_attempt (id int)"
expect_deny "cannot create tables in core"                ai_app "$AI_APP_PASSWORD" "CREATE TABLE core._ddl_attempt (id int)"
expect_deny "cannot create tables in public"              ai_app "$AI_APP_PASSWORD" "CREATE TABLE public._ddl_attempt (id int)"
expect_deny "cannot drop ai tables"                       ai_app "$AI_APP_PASSWORD" "DROP TABLE ai._role_check"

echo "migrators"
expect_ok   "core_migrator can run DDL in core"           core_migrator "$CORE_MIGRATOR_PASSWORD" "CREATE TABLE core._ddl_ok (id int); DROP TABLE core._ddl_ok"
expect_deny "core_migrator cannot touch ai"               core_migrator "$CORE_MIGRATOR_PASSWORD" "SELECT * FROM ai._role_check"
expect_ok   "ai_migrator can run DDL in ai"               ai_migrator "$AI_MIGRATOR_PASSWORD" "CREATE TABLE ai._ddl_ok (id int); DROP TABLE ai._ddl_ok"
expect_deny "ai_migrator cannot touch core"               ai_migrator "$AI_MIGRATOR_PASSWORD" "SELECT * FROM core._role_check"
expect_deny "ai_migrator cannot create tables in core"    ai_migrator "$AI_MIGRATOR_PASSWORD" "CREATE TABLE core._ddl_attempt (id int)"

echo
echo "$pass passed, $fail failed"
[ "$fail" -eq 0 ]
