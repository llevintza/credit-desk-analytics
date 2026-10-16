# Dapper grid SQL

Linked from [`src/Desk.Data/AGENTS.md`](../../../src/Desk.Data/AGENTS.md). Root SQL-safety rule still applies; this page is the how-to.

## Where

- Dapper 2.1.89 reads: `src/Desk.Data/Grid/{GridRepository,GridSqlBuilder,GridQueryNormalizer,MetaRepository,PresetRepository}.cs`.

## Identifiers and values

- Identifiers only through `Catalog/ColumnCatalog.cs` / `Grid/GridColumns.cs`.
- Values are always parameters. Portfolio scope is `portfolio_id = ANY(@portfolios)`.

## Filters and export

- A well-formed filter that can't be applied as asked throws `GridRequestException`, which becomes a 400 "Filter can't be applied" with the reason in `detail` (`PositionsEndpoints.Resolve`): more than 50 filters or 10 conditions, a set list over 25,000 values, text over 200 characters, a combined filter with a part that can't be applied, or a money or number value beyond `decimal`'s or `double`'s range. The `GridQueryNormalizer` class summary and its `Max*` constants are the source. Never silently drop a filter, because that widens the result.
- `DbConnectionCounter` lets tests assert DB-free paths (a 304 or cache hit).
- Export streams via `StreamAsync`, capped at `MaxExportRows` = 25,000, one export per user.

## Tests

- `tests/Desk.Data.Tests/GridQueryTests.cs` and `tests/Desk.Api.Tests/Positions*Tests.cs`.
