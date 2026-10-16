using Dapper;
using Desk.Data.App;
using Desk.Data.Catalog;
using Desk.Data.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desk.Data.Grid;

public sealed record PortfolioInfo(int PortfolioId, string Name, int FundId, string FundName);

/// <summary>Reference data that changes only with a reseed: the column catalog, as-of dates, portfolios, data version.</summary>
public sealed class MetaRepository(IDbContextFactory<AppDbContext> contexts, IDataSourceRegistry sources, ILogger<MetaRepository>? logger = null)
{
    private readonly ILogger _logger = logger ?? NullLogger<MetaRepository>.Instance;

    /// <summary>The column catalog from <c>app.column_catalog</c> (README §5.3: the catalog is data), in display order.</summary>
    public async Task<IReadOnlyList<ColumnDef>> CatalogAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var rows = await db.ColumnCatalog.AsNoTracking()
            .OrderBy(c => c.Ordinal)
            .Select(c => new { c.Name, c.Group, c.Kind, c.Aggregation, c.Header })
            .ToListAsync(ct);
        var unknown = rows.Count(r => !Known(r.Aggregation));
        if (unknown > 0)
            _logger.LogWarning("Column catalog has {Count} aggregations this build doesn't know; their totals are |MV|-weighted until it's updated", unknown);
        return rows.Select(r => new ColumnDef(r.Name, r.Group, Enum.Parse<ColumnKind>(r.Kind), ParseAggregation(r.Aggregation), r.Header)).ToArray();
    }

    /// <summary>
    /// A catalog row's aggregation. A name this build doesn't know (a newer seed or data migration that ran before this
    /// app version was replaced, README §14.4) falls back to the |MV|-weighted average instead of failing every meta
    /// load (#459). Only exact enum names count: numbers and other casings are unknown too.
    /// </summary>
    internal static Aggregation ParseAggregation(string value) =>
        Known(value) ? Enum.Parse<Aggregation>(value) : Aggregation.WeightedByMarketValue;

    private static bool Known(string value) => Enum.GetNames<Aggregation>().Contains(value, StringComparer.Ordinal);

    /// <summary>Identifies the loaded data: changes on every reseed, so ETags from an older load never match.</summary>
    public async Task<string> DataVersionAsync(CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        var latest = await db.SeedMetadata.AsNoTracking()
            .OrderByDescending(m => m.CompletedAt)
            .Select(m => new { m.Id, m.Version })
            .FirstOrDefaultAsync(ct);
        return latest is null ? "empty" : $"{latest.Version}.{latest.Id}";
    }

    /// <summary>The snapshot's as-of dates, newest first.</summary>
    public async Task<IReadOnlyList<DateOnly>> AsOfDatesAsync(CancellationToken ct)
    {
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        var dates = await conn.QueryAsync<DateOnly>(new CommandDefinition(
            "SELECT DISTINCT as_of_date FROM core.position_snapshot ORDER BY as_of_date DESC", cancellationToken: ct));
        return dates.ToArray();
    }

    public async Task<IReadOnlyList<PortfolioInfo>> PortfoliosAsync(CancellationToken ct)
    {
        await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
        var rows = await conn.QueryAsync<PortfolioInfo>(new CommandDefinition(
            """
            SELECT p.portfolio_id AS PortfolioId, p.name AS Name, f.fund_id AS FundId, f.name AS FundName
            FROM core.portfolio p JOIN core.fund f ON f.fund_id = p.fund_id
            ORDER BY p.portfolio_id
            """, cancellationToken: ct));
        return rows.ToArray();
    }
}
