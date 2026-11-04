#!/usr/bin/env bash
# Runs Schemathesis against a running Core API (Doc 02 §8.1, NFR-MAIN-02).
#
#   contracts/run-schemathesis.sh [base-url]        default http://localhost:8080
#
# It registers a throw-away user, passes that user's session as headers, and uses contracts/schemathesis.toml.
# Start the API with generous rate limits first (otherwise most answers are 429), for example:
#   Auth__RateLimits__RegisterPerIpPerHour=100000 Auth__RateLimits__GeneralPerUserPerMinute=1000000 \
#   Auth__RateLimits__PreviewPerUserPerMinute=1000000 Auth__RateLimits__ExportPerUserPerMinute=1000000
# The admin endpoints are answered with 403 for this member, which the contract allows.
set -euo pipefail
base="${1:-http://localhost:8080}"
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
jar="$(mktemp)"
trap 'rm -f "$jar"' EXIT

cookie() { awk -v n="$1" '$6==n {print $7}' "$jar"; }

curl -fsS -c "$jar" -o /dev/null "$base/api/v1/auth/csrf"
csrf="$(cookie kh_csrf)"
curl -fsS -b "$jar" -c "$jar" -H "X-CSRF-Token: $csrf" -H 'Content-Type: application/json' \
  -d "{\"email\":\"contract-$RANDOM$RANDOM@example.com\",\"password\":\"a long unusual passphrase\",\"display_name\":\"Contract test\"}" \
  -o /dev/null "$base/api/v1/auth/register"
token="$(cookie kh_at)"
csrf="$(cookie kh_csrf)"

# Schemathesis keeps a cache in the directory it runs from; keep it out of the repository.
cd "$(mktemp -d)"

# Health checks live outside /api/v1, saved views and OAuth are not built yet (Could), and the session-ending calls
# would invalidate the cookie in the middle of the run.
exec uvx --from 'schemathesis==4.33.0' schemathesis --config-file "$here/schemathesis.toml" run "$here/openapi.yaml" \
  --url "$base/api/v1" \
  --exclude-path-regex '^/health/|^/views|^/auth/oauth|^/(auth/(logout|login|register|refresh)|me$)' \
  -H "Cookie: kh_at=$token; kh_csrf=$csrf" -H "X-CSRF-Token: $csrf"
