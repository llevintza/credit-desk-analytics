# syntax=docker/dockerfile:1.7
# Multi-stage: Angular build -> .NET publish (SPA copied into wwwroot) -> slim runtime (README §13.2).

FROM node:22-alpine@sha256:0a7108bf6c7bf5de370ffb1a3ed6be93d405b43ff159f681a8d18c0e2bc2e402 AS web
WORKDIR /web
COPY web/package.json web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY web/ ./
RUN npm run build

FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317 AS api
WORKDIR /src
COPY global.json Directory.Build.props CreditDesk.slnx ./
COPY src/Desk.Api/Desk.Api.csproj src/Desk.Api/
COPY src/Desk.Data/Desk.Data.csproj src/Desk.Data/
RUN dotnet restore src/Desk.Api/Desk.Api.csproj
COPY src/ src/
RUN dotnet publish src/Desk.Api/Desk.Api.csproj -c Release -o /out --no-restore \
 && rm -rf /out/wwwroot
COPY --from=web /web/dist/web/browser /out/wwwroot

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:222759b391a1aaf241166672c8f99b2d4ada452e7b5319f3c6e8f265a37b5ad4 AS runtime
WORKDIR /app
COPY --from=api /out ./
COPY deploy/start.sh ./start.sh
# Local builds can stamp a version; on Render, start.sh falls back to RENDER_GIT_COMMIT.
ARG APP_VERSION=dev
ENV APP_VERSION=${APP_VERSION} \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    ASPNETCORE_ENVIRONMENT=Production
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["sh", "./start.sh"]
