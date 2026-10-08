# Dapper grid SQL

Linked from [`src/Desk.Data/AGENTS.md`](../../../src/Desk.Data/AGENTS.md). Root SQL-safety rule still applies; this page is the how-to.

## Where

- Dapper 2.1.89 reads: `src/Desk.Data/Grid/{GridRepository,GridSqlBuilder,GridQueryNormalizer,MetaRepository,PresetRepository}.cs`.

## Identifiers and values

- Identifiers only through `Catalog/ColumnCatalog.cs` / `Grid/GridColumns.cs`.
- Values are always parameters. Portfolio scope is `portfolio_id = ANY(@portfolios)`.

## Filters and export

- An oversized filter throws `GridRequestException`, which becomes a 400 "Filter too large". Never silently drop a filter, because that widens the result.
- `DbConnectionCounter` lets tests assert DB-free paths (a 304 or cache hit).
- Export streams via `StreamAsync`, capped at `MaxExportRows` = 25,000, one export per user.

## Tests

- `tests/Desk.Data.Tests/GridQueryTests.cs` and `tests/Desk.Api.Tests/Positions*Tests.cs`.
