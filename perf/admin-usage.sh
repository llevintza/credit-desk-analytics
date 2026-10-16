#!/usr/bin/env bash
# ADR-0027: runs perf/admin-usage.sql in a throwaway local Postgres 17 container with trust auth: one fill of 90 days,
# then three warm passes over 1-, 7- and 30-day windows. Never Neon or production.
# Usage: perf/admin-usage.sh [rows_per_day]   (default 10000)
set -euo pipefail

IMAGE=postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24
NAME=admin-usage-bench
ROWS=${1:-10000}
PERF_DIR=$(cd "$(dirname "$0")" && pwd)

wait_ready() {
  for _ in $(seq 1 60); do
    if docker logs "$NAME" 2>&1 | grep -q "PostgreSQL init process complete" && docker exec "$NAME" pg_isready -U postgres -q; then
      return 0
    fi
    sleep 1
  done
  echo "postgres did not become ready" >&2
  return 1
}

run() { docker exec "$NAME" psql -U postgres -X -q -v rows_per_day="$ROWS" -v days="$1" -v fill="$2" -f /perf/admin-usage.sql; }

docker rm -f -v "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -e POSTGRES_HOST_AUTH_METHOD=trust -v "$PERF_DIR":/perf:ro "$IMAGE" >/dev/null
trap 'docker rm -f -v "$NAME" >/dev/null' EXIT  # -v: the image's data volume goes too
wait_ready

run 1 on >/dev/null
for days in 1 7 30; do
  for pass in 1 2 3; do
    echo "===== days=$days pass=$pass (rows_per_day=$ROWS)"
    run "$days" off | grep -E '^--- |Execution Time|Sort Method'
  done
done
