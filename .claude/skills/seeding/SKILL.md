---
name: seeding
description: "Change the synthetic data seeder: --if-changed, bump SeedVersion when the generator or seeded schema changes, COPY bulk load, < 90 s and < 350 MB, synthetic names only."
paths:
  - "src/Desk.Seeder/**"
---
# Change the seeder

1. Read [`src/Desk.Seeder/AGENTS.md`](../../../src/Desk.Seeder/AGENTS.md).
2. Use `--if-changed` for deploy-safe skips. `--force` wipes seeded tables; local only (root rule).
3. Bump `SeedVersion` when the generator or the seeded schema changes.
4. COPY bulk load. Stay under 90 s and 350 MB; the seeder fails above 400 MB.
5. Synthetic names only. No real company, employer, client or person names.
