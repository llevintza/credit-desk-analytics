#!/usr/bin/env bash
# ADR-0022: runs perf/audit-retention.sql warm (one pass) and cold (Postgres restart + OS page cache dropped before
# each measured step) in a throwaway local Postgres 17 container with trust auth. Never Neon or production.
# Usage: perf/audit-retention.sh [rows_per_day]   (default 10000)
set -euo pipefail

IMAGE=postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24
NAME=audit-retention-bench
ROWS=${1:-10000}
PERF_DIR=$(cd "$(dirname "$0")" && pwd)

wait_ready() {
  # The image's first start runs a temporary server for initdb scripts; wait until that one is gone.
  for _ in $(seq 1 60); do
    if docker logs "$NAME" 2>&1 | grep -q "PostgreSQL init process complete" && docker exec "$NAME" pg_isready -U postgres -q; then
      return 0
    fi
    sleep 1
  done
  echo "postgres did not become ready" >&2
  return 1
}

run() { docker exec "$NAME" psql -U postgres -X -q -v rows_per_day="$ROWS" -v step="$1" -f /perf/audit-retention.sql; }

cold() {
  docker restart "$NAME" >/dev/null
  # Drops the Docker VM's page cache, so the next step reads the table from disk.
  docker run --rm --privileged "$IMAGE" sh -c 'sync; echo 3 > /proc/sys/vm/drop_caches'
  wait_ready
}

docker rm -f "$NAME" >/dev/null 2>&1 || true
docker run -d --name "$NAME" -e POSTGRES_HOST_AUTH_METHOD=trust -v "$PERF_DIR":/perf:ro "$IMAGE" >/dev/null
trap 'docker rm -f "$NAME" >/dev/null' EXIT
wait_ready

echo "===== warm (rows_per_day=$ROWS)"
run all

echo "===== cold (rows_per_day=$ROWS)"
run fill >/dev/null
for step in daily backlog batch batchin; do
  cold
  run "$step"
done
