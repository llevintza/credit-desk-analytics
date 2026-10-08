using System.Diagnostics;
using Desk.Data.Catalog;
using Desk.Data.Grid;

namespace Desk.Api.Positions;

/// <summary>Reference data: the catalog (and its whitelist), as-of dates, data version, portfolios.</summary>
/// <param name="ExpiresAt">When this snapshot is re-read (see <see cref="MetaCache"/>).</param>
/// <param name="BatchEndsAt">The next 06:30 New York batch: cached responses never outlive it.</param>
public sealed record MetaSnapshot(
    IReadOnlyList<ColumnDef> Catalog,
    GridQueryNormalizer? Normalizer,
    IReadOnlyList<DateOnly> AsOfDates,
    string DataVersion,
    IReadOnlyList<PortfolioInfo> Portfolios,
    DateTimeOffset ExpiresAt,
    DateTimeOffset BatchEndsAt)
{
    /// <summary>False until the seeder has loaded the catalog and at least one as-of date.</summary>
    public bool HasData => Normalizer is not null && AsOfDates.Count > 0;
}

/// <summary>
/// Loads <see cref="MetaSnapshot"/> cache-first (README §7.2). It's re-read every <see cref="Revalidate"/> while
/// requests come in, so a reseed (new data version) changes every cache key and ETag within minutes without a
/// restart, and every <see cref="RetryEmpty"/> while the database isn't seeded yet. Concurrent requests share one
/// load; the four reads run in parallel on their own connections.
/// </summary>
public sealed class MetaCache(MetaRepository repo, TimeProvider time)
{
    public static readonly TimeSpan Revalidate = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetryEmpty = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private MetaSnapshot? _current;

    public async ValueTask<MetaSnapshot> GetAsync(CancellationToken ct) => (await GetWithStatusAsync(ct)).Snapshot;

    /// <returns>The snapshot, and how long it took to load from the database when this call loaded it (else null).</returns>
    public async ValueTask<(MetaSnapshot Snapshot, double? LoadMs)> GetWithStatusAsync(CancellationToken ct)
    {
        if (Fresh() is { } fresh)
            return (fresh, null);

        await _gate.WaitAsync(ct);
        try
        {
            if (Fresh() is { } loaded)
                return (loaded, null);

            var started = Stopwatch.GetTimestamp();
            var catalog = repo.CatalogAsync(ct);
            var dates = repo.AsOfDatesAsync(ct);
            var version = repo.DataVersionAsync(ct);
            var portfolios = repo.PortfoliosAsync(ct);
            await Task.WhenAll(catalog, dates, version, portfolios);

            var now = time.GetUtcNow();
            var batchEnd = BatchClock.NextBatchAfter(now);
            var normalizer = catalog.Result.Any(c => c.Name == GridQueryNormalizer.RowIdColumn) ? new GridQueryNormalizer(catalog.Result) : null;
            var snapshot = new MetaSnapshot(catalog.Result, normalizer, dates.Result, version.Result, portfolios.Result, now, batchEnd);
            _current = snapshot with { ExpiresAt = Min(now + (snapshot.HasData ? Revalidate : RetryEmpty), batchEnd) };
            return (_current, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the snapshot so the next request reloads it (admin cache clear, tests).</summary>
    public void Invalidate() => _current = null;

    private MetaSnapshot? Fresh() => _current is { } s && time.GetUtcNow() < s.ExpiresAt ? s : null;

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
