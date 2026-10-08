using System.Diagnostics;
using System.Text.RegularExpressions;
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
public sealed partial class MetaCache(MetaRepository repo, TimeProvider time, ILogger<MetaCache>? logger = null)
{
    private readonly ILogger _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<MetaCache>.Instance;

    public static readonly TimeSpan Revalidate = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan RetryEmpty = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    // Read without the gate on the fast path; volatile so a reader never sees a stale reference.
    private volatile MetaSnapshot? _current;

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
            var columns = await catalog;

            var now = time.GetUtcNow();
            var batchEnd = BatchClock.NextBatchAfter(now);
            // The catalog's names become quoted SQL identifiers: anything but snake_case means the table was tampered
            // with, so the grid stays unavailable instead of building SQL from it (#130 N2).
            var unsafeNames = columns.Where(c => !SafeName().IsMatch(c.Name)).Select(c => c.Name).ToList();
            if (unsafeNames.Count > 0)
                _logger.LogError("Column catalog has {Count} names that aren't snake_case identifiers; the grid is unavailable until it's fixed", unsafeNames.Count);
            var normalizer = unsafeNames.Count == 0 && columns.Any(c => c.Name == GridQueryNormalizer.RowIdColumn) ? new GridQueryNormalizer(columns) : null;
            var snapshot = new MetaSnapshot(columns, normalizer, await dates, await version, await portfolios, now, batchEnd);
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

    [GeneratedRegex(@"^[a-z_][a-z0-9_]*\z")] // \z, not $: $ also matches before a trailing newline
    private static partial Regex SafeName();
}
