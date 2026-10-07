#!/usr/bin/env sh
# Starts the app only. Migrations and seeding run in GitHub Actions before the deploy
# (README §14.2), so a free-tier cold start never runs DDL.
set -e

# Fail with a message that names the variable, instead of a connection error at the first query.
if [ -z "${DATABASE_URL:-}" ] && [ -z "${ConnectionStrings__App:-}" ]; then
  echo "ERROR: DATABASE_URL is not set. Set it on the service before deploying." >&2
  exit 1
fi

# /health reports this; the deploy pipeline waits until it equals the merged commit.
if [ "${APP_VERSION:-dev}" = "dev" ] && [ -n "${RENDER_GIT_COMMIT:-}" ]; then
  export APP_VERSION="$RENDER_GIT_COMMIT"
fi

# Render assigns PORT; locally the image listens on 8080.
export ASPNETCORE_HTTP_PORTS="${PORT:-8080}"

exec dotnet Desk.Api.dll
