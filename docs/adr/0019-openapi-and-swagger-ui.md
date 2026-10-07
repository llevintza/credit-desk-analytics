# ADR-0019: OpenAPI document + Swagger UI for exploring the API

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** cross-cutting (#93)

## Context

The owner wants a page where every API endpoint can be explored and tried (README §8: "OpenAPI-documented"). The site is public, and accounts arrive in phase 2. The SPA's bundle budget (README §10) must not be affected.

## Options considered

1. **Built-in `Microsoft.AspNetCore.OpenApi` (`AddOpenApi`/`MapOpenApi`) + Swashbuckle's SwaggerUI package only:** the framework generates the document, and a familiar UI reads it.
2. **Swashbuckle end to end** (its own generator + UI): mature, but a second generator alongside the framework's, and .NET 9+ templates moved away from it.
3. **Built-in document + Scalar UI:** a modern UI, but less familiar to most API consumers, and "Try it out" works differently.
4. **NSwag:** generator + UI + client codegen. Heavier than needed. Client generation for the Angular app is a separate decision (phase 4).

## Evaluation

Release build, local, 5 runs each, Apple M5:

| Criterion | Measured / assessed |
|---|---|
| Startup to first `/health` | Swagger off median **207 ms** (203–283) vs on median **218 ms** (210–250): +~10 ms, within noise |
| OpenAPI document size | **1,076 bytes** today (2 endpoints) |
| Swagger UI bundle (`swagger-ui-bundle.js`) | 1,586,002 bytes raw, **495,160 bytes with Brotli** (response compression applies). Loaded **only** on `/swagger`; never part of the SPA's initial bundle (README §10 budget unaffected) |
| Packages | `Microsoft.AspNetCore.OpenApi` (framework) + `Swashbuckle.AspNetCore.SwaggerUI` (static UI assets only) |
| Familiarity / "Try it out" | Standard Swagger UI |

**How to reproduce:** `dotnet build -c Release src/Desk.Api`, then start the DLL with `SWAGGER_ENABLED=true|false` and time the first successful `GET /health`. Fetch `/openapi/v1.json` and `/swagger/swagger-ui-bundle.js` with and without `Accept-Encoding: br`.

## Decision

Option 1:
- **Endpoints:** `/openapi/v1.json` (OpenAPI 3.1) and Swagger UI at `/swagger`.
- **When it's on:** in Development; elsewhere only when `SWAGGER_ENABLED=true`, set in the Render dashboard when the deployed API needs testing.
- **Metadata:** every endpoint carries a name, summary and tag, and the SPA and `/api` fallbacks are excluded.

## Consequences

- **Off by default in production:** the public site exposes no API description unless the owner turns it on.
- **Phase 2 (#94)** puts both paths behind the `admin` role and adds a CSP exception for Swagger UI's assets on `/swagger` only.
- **Keeping the document useful:** new endpoints must add OpenAPI metadata. That rule belongs in AGENTS.md, which needs its own `[workflows]` PR.
