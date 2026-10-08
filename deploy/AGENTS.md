# deploy: images and start-up

Area file for `deploy/` (the app Dockerfile, `start.sh`, and the pinned CI tool images under `ci-images/`). The root `AGENTS.md` still applies in full. Spec: README §13 and §14.2.

## Every change here is governed

- Dockerfile changes fall under gate clause 3, and `deploy/**` is a governance path: use a `[workflows]` PR with governance review. The same holds for the root `render.yaml` and `docker-compose.yml`, which this file doesn't cover.

## Rules

- `start.sh` checks the environment and execs the app. It never runs migrations, DDL or seeding (README §4, §14.4).
- `Dockerfile` is multi-stage: node builds `web`, `dotnet publish`, runtime image.
- No credentials or `Password=…` literals in any Dockerfile, compose file or script.
- The runtime image publishes only `Desk.Api`, and the db-tools bundle only efbundle and the seeder. Never add `Desk.UserAdmin` to either, or to a workflow that holds production secrets (ADR-0020 §1, H19).
- `ci-images/*/Dockerfile` pins the CI tools. Keep each pin in step with the workflows that run it (`.github/AGENTS.md`).

## Deploy facts agents must not forget

- Merging to `main` deploys: CI → migrate Neon → seed if `SeedVersion` changed → Render deploy hook → smoke test on `/health`.
- There is no approval pause and no automatic rollback; the migration has already run when smoke runs (ADR-0020 §8 H6).
- Production incidents use Render rollback, coordinated by Helms and carried out by Tech Coordinator (ADR-0020 §8 H15). Agents don't trigger deploys or database operations.
