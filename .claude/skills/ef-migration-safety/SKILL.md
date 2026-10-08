---
name: ef-migration-safety
description: "Add or review an EF Core migration in src/Desk.Data: expand, deploy, contract; nullable or defaulted columns; no rename or drop in one release; no seed data; migrate twice."
paths:
  - "src/Desk.Data/**"
---
# EF migration safety

Follow [`docs/agents/guidelines/ef-postgres-migrations.md`](../../../docs/agents/guidelines/ef-postgres-migrations.md) and [`src/Desk.Data/AGENTS.md`](../../../src/Desk.Data/AGENTS.md).

1. Expand → deploy → contract (README §14.4). Nullable or defaulted columns. No rename or drop in the same release.
2. No seed data in migrations. Bump `SeedVersion` if the seeded schema changes.
3. `dotnet ef migrations has-pending-model-changes` must be false.
4. Migrate twice locally (second run is a no-op), same as CI `api`.
5. Local stack only (root rule). Never point `dotnet ef` at Neon or production.
