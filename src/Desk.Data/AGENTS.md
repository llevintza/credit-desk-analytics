# src/Desk.Data: data access, EF Core and migrations

Area file for `src/Desk.Data/` (EF Core DbContexts and migrations, Dapper query builders, the column catalog). The root `AGENTS.md` still applies in full. Spec: README §5 and §14.4. Seeding rules: `src/Desk.Seeder/AGENTS.md`.

## SQL safety

- Client-supplied identifiers (columns, sort, filter keys) are resolved through the column-catalog whitelist (`Catalog/ColumnCatalog.cs`).
- Every value is a parameter. Never concatenate client input into SQL.

## EF Core

- Never share a DbContext across concurrent operations; use `IDbContextFactory` per parallel task.
- Use `AsNoTracking` + projections for reads.
- No captive dependencies (a scoped context inside a singleton).

## Migrations

- Migrations run **before** the new app version starts (README §14.2), so each one must be backward compatible with the running app: expand → deploy → contract (README §14.4).
- Use the `ef-migration-safety` skill (or the `ef-migration` role) for any schema change.
- A change to the seeded schema also needs a `SeedVersion` bump (`src/Desk.Seeder/AGENTS.md`); otherwise production keeps the old data.
- Never run DDL from the app at start-up.

## Commands

| Task | Command |
|---|---|
| Apply migrations (local) | `dotnet ef database update --project src/Desk.Data --startup-project src/Desk.Data` |
| Add a migration | `dotnet ef migrations add <Name> --project src/Desk.Data --startup-project src/Desk.Data --output-dir App/Migrations` |

Run `dotnet tool restore` first (root commands). Never point either command at production or Neon.

## Tests

- `tests/Desk.Data.Tests`. Money stays `decimal` / `numeric`; zero or empty weights give `null`, never `NaN`.
