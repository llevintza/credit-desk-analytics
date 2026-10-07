# ADR-0002: Hosting: single Render web service + Neon Postgres

- **Status:** Accepted
- **Date:** 2026-10-07
- **Phase / PR:** phase-0/scaffold

## Context

The repo is private. The website must be public, with the data behind invite-only accounts (README §7). The budget is free-tier first. Earlier personal projects used GitHub Pages (frontend) + Render (API) + Neon (DB).

## Options considered

1. **GitHub Pages (SPA) + Render (API) + Neon.**
2. **Render static site (SPA) + Render web service (API) + Neon.**
3. **One Render Docker web service serving the SPA and the API from one origin + Neon.**

## Evaluation

| Criterion | 1. Pages + Render | 2. Static site + API | 3. Single service |
|---|---|---|---|
| Works from a **private** repo on free plans | No (Pages needs a paid plan for private repos) | Yes | Yes |
| Auth cookies | Cross-site: `SameSite=None` and third-party cookie blocking | Cross-site between two `onrender.com` subdomains (the public suffix list makes them different sites) | **Same-origin `SameSite=Strict`** |
| CORS | Required | Required | **None** |
| Free instances consumed | 1 | 1 (+ static) | 1 |
| Cold start affects the login page | No | No | Yes, mitigated by the "Waking the server…" state |
| CDN for static assets | Yes | Yes | No (hashed assets, long cache, Brotli) |

## Decision

Option 3: one Render Docker web service (ASP.NET Core serves the built SPA from `wwwroot`, with `/api/*`), plus Neon Postgres via the **direct** endpoint.

## Consequences

- The simplest secure auth: no tokens in JavaScript, no CORS configuration.
- The SPA is unavailable while the free instance cold-starts (30–60 s). Accepted for about 60 internal users; switch the plan to `starter` for demo windows.
- There's no CDN, but at a 59 kB initial transfer that doesn't matter.
- Render has no native .NET runtime, so the service is Docker-based (`deploy/Dockerfile`).
