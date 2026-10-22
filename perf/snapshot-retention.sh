#!/usr/bin/env bash
# ADR-0029 (#544): measures the storage of a daily as-of roll under the two D13 retention mechanisms. For each mode it
# starts a throwaway local Postgres 17 container (trust auth), applies the EF migrations, seeds scale 1.0 with
# Desk.Seeder, then runs perf/snapshot-retention.sql for N simulated business days. Never Neon or production.
# Usage: perf/snapshot-retention.sh [days] [port]   (default 10 days, port 55432)
set -euo pipefail

IMAGE=postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24
NAME=snapshot-retention-bench
DAYS=${1:-10}
PORT=${2:-55432}
ROOT=$(cd "$(dirname "$0")/.." && pwd)
export DATABASE_URL="Host=localhost;Port=$PORT;Username=postgres;Database=desk"

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

# run <mode> <step> [day] [extra psql flag]
run() { docker exec "$NAME" psql -U postgres -d desk -X -q -P footer=off ${4:-} -v mode="$1" -v step="$2" -v day="${3:-0}" -f /perf/snapshot-retention.sql; }

trap 'docker rm -f "$NAME" >/dev/null 2>&1' EXIT
(cd "$ROOT" && dotnet tool restore >/dev/null && dotnet build -c Release src/Desk.Seeder >/dev/null)

for mode in delete partition; do
  docker rm -f "$NAME" >/dev/null 2>&1 || true
  docker run -d --name "$NAME" -p "127.0.0.1:$PORT:5432" -e POSTGRES_HOST_AUTH_METHOD=trust -e POSTGRES_DB=desk \
    -v "$ROOT/perf":/perf:ro "$IMAGE" >/dev/null
  wait_ready
  (cd "$ROOT" && dotnet ef database update --project src/Desk.Data --startup-project src/Desk.Data >/dev/null)
  (cd "$ROOT" && dotnet run -c Release --no-build --project src/Desk.Seeder -- --if-changed --scale 1.0 --as-of 2026-10-06 \
    | grep -E 'DB_SIZE_MB|ELAPSED_S')
  docker exec "$NAME" psql -U postgres -d desk -X -q -c 'VACUUM (ANALYZE);'

  echo "===== mode=$mode, $DAYS business days"
  if [ "$mode" = partition ]; then run "$mode" partition; fi
  run "$mode" size
  for day in $(seq 1 "$DAYS"); do
    # One header for the table: only the first day prints it, the rest print tuples only.
    if [ "$day" -eq 1 ]; then run "$mode" day "$day" | sed '/^$/d'; else run "$mode" day "$day" -t | sed '/^$/d'; fi
  done
  run "$mode" size
done
