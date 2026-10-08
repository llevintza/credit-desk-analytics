# Playwright e2e

Linked from the root E2E command row. The local-only line in root `AGENTS.md` applies; this page does not restate it.

## Config

- `e2e/playwright.config.ts`: `testDir` `./tests`, 1 worker, CI retries 1.
- `setup` project (`auth.setup.ts` writes `.auth/state.json`) then `chromium`.
- Viewport 1600×900. `BASE_URL` default `http://localhost:8080`.

## Specs

- `positions.spec.ts`
- `screenshots.spec.ts` (dark/light into `docs/screenshots`)
- `perf.spec.ts` (`PERF=1`, ADR-0009)

## Stack

- compose + `e2e/docker-compose.e2e.yml`, seed `--scale 0.2`, throwaway viewer from Desk.UserAdmin.
- `DESK_EMAIL` / `DESK_PASSWORD` in env only.
- Keep `.auth/` out of git (`e2e/.gitignore`).
