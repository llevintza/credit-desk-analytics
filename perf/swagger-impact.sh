#!/usr/bin/env bash
# ADR-0019: measure what Swagger adds. Startup to first /health with SWAGGER_ENABLED=false vs true
# (Production environment, Release build, N runs each), plus OpenAPI document and Swagger UI bundle sizes
# with and without Brotli. No database is touched (/health is static); the connection string is a dummy.
# Usage: perf/swagger-impact.sh [runs=5] [port=5201]
set -euo pipefail
RUNS="${1:-5}"; PORT="${2:-5201}"
cd "$(dirname "$0")/.."
dotnet build -c Release src/Desk.Api >/dev/null
DLL=src/Desk.Api/bin/Release/net10.0/Desk.Api.dll
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT
now_ms() { python3 -c 'import time; print(int(time.time() * 1000))'; }

startup() { # $1 = true|false; prints one ms value per run, then leaves the last instance running when $2=keep
  local on="$1" keep="${2:-}" ms=()
  for i in $(seq 1 "$RUNS"); do
    local t0; t0="$(now_ms)"
    SWAGGER_ENABLED="$on" ASPNETCORE_ENVIRONMENT=Production DATABASE_URL="Host=unused" \
      dotnet "$DLL" --urls "http://localhost:$PORT" >/dev/null 2>&1 &
    local pid=$!
    until curl -fs "localhost:$PORT/health" >/dev/null 2>&1; do sleep 0.02; done
    ms+=("$(( $(now_ms) - t0 ))")
    if [[ "$keep" == keep && "$i" == "$RUNS" ]]; then echo "$pid" > "$TMP/pid"; else kill "$pid"; wait "$pid" 2>/dev/null || true; fi
  done
  printf '%s\n' "${ms[@]}" | sort -n | awk -v on="$on" '{a[NR]=$1} END {printf "startup_ms SWAGGER_ENABLED=%s runs=%d median=%d min=%d max=%d\n", on, NR, a[int((NR+1)/2)], a[1], a[NR]}'
}

startup false
startup true keep
size() { curl -s "$@" -o "$TMP/body" -w '%{size_download}'; }
echo "openapi_json_bytes=$(size "localhost:$PORT/openapi/v1.json")"
echo "swagger_ui_bundle_bytes_raw=$(size "localhost:$PORT/swagger/swagger-ui-bundle.js")"
echo "swagger_ui_bundle_bytes_br=$(size -H 'Accept-Encoding: br' "localhost:$PORT/swagger/swagger-ui-bundle.js")"
echo "spa_index_mentions_swagger=$(curl -s "localhost:$PORT/" | grep -ci swagger || true)"
kill "$(cat "$TMP/pid")"; wait "$(cat "$TMP/pid")" 2>/dev/null || true
