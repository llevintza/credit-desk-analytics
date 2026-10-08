using Desk.Data.Catalog;
using Desk.Data.Grid;

namespace Desk.Api.Positions;

/// <summary>Reference data for one batch window: the catalog (and its whitelist), as-of dates, data version, portfolios.</summary>
public sealed record MetaSnapshot(
    IReadOnlyList<ColumnDef> Catalog,
    GridQueryNormalizer? Normalizer,
    IReadOnlyList<DateOnly> AsOfDates,
    string DataVersion,
    IReadOnlyList<PortfolioInfo> Portfolios,
    DateTimeOffset ExpiresAt)
{
    /// <summary>False until the seeder has loaded the catalog and at least one as-of date.</summary>
    public bool HasData => Normalizer is not null && AsOfDates.Count > 0;
}

/// <summary>
/// Loads <see cref="MetaSnapshot"/> once per batch window (cache-first, README §7.2). Concurrent first requests
/// share one load.
/// </summary>
public sealed class MetaCache(MetaRepository repo, TimeProvider time)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MetaSnapshot? _current;

    public async ValueTask<MetaSnapshot> GetAsync(CancellationToken ct)
    {
        if (_current is { } fresh && time.GetUtcNow() < fresh.ExpiresAt)
            return fresh;

        await _gate.WaitAsync(ct);
        try
        {
            if (_current is { } loaded && time.GetUtcNow() < loaded.ExpiresAt)
                return loaded;

            var catalog = await repo.CatalogAsync(ct);
            var normalizer = catalog.Any(c => c.Name == GridQueryNormalizer.RowIdColumn) ? new GridQueryNormalizer(catalog) : null;
            _current = new MetaSnapshot(catalog, normalizer, await repo.AsOfDatesAsync(ct), await repo.DataVersionAsync(ct),
                await repo.PortfoliosAsync(ct), BatchClock.NextBatchAfter(time.GetUtcNow()));
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the snapshot so the next request reloads it (after a reseed, or from tests).</summary>
    public void Invalidate() => _current = null;
}
