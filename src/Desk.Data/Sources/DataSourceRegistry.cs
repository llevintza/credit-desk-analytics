using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Desk.Data.Sources;

/// <summary>
/// Resolves a connection per logical data source (README §5.1). Every source has its own setting
/// (<c>ConnectionStrings__Core</c>, …) falling back to <c>DATABASE_URL</c>; sources that resolve to the same
/// connection string share one <see cref="NpgsqlDataSource"/> (one pool). Moving a source to another database is
/// a config change only.
/// </summary>
public interface IDataSourceRegistry
{
    NpgsqlDataSource Get(string source);

    /// <summary>Opens a pooled connection for <paramref name="source"/> and counts it (<see cref="DbConnectionCounter"/>).</summary>
    ValueTask<NpgsqlConnection> OpenAsync(string source, CancellationToken ct);
}

public sealed class DataSourceRegistry(IConfiguration config, DbConnectionCounter counter) : IDataSourceRegistry, IAsyncDisposable, IDisposable
{
    // Keyed by the normalized connection string, so two sources pointing at one database share a pool.
    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> _byConnectionString = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sourceToConnectionString = new(StringComparer.Ordinal);
    private readonly int _maxPoolSize = MaxPoolSizeFrom(config);

    public NpgsqlDataSource Get(string source)
    {
        // Resolved on first use, not at startup: the app boots (and /health answers) without a database.
        var cs = _sourceToConnectionString.GetOrAdd(source, s => Normalize(ConnectionStrings.Resolve(config, s), _maxPoolSize));
        return _byConnectionString.GetOrAdd(cs, c => new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(c))).Value;
    }

    public async ValueTask<NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
    {
        var conn = await Get(source).OpenConnectionAsync(ct);
        counter.Record();
        return conn;
    }

    /// <summary>Npgsql's own default is 100, more than a small Neon compute's <c>max_connections</c> allows (README §13.1).</summary>
    public const int DefaultMaxPoolSize = 20;

    /// <summary>
    /// README §7.2: no data query runs longer than 10 s. README §13.1: every pool is capped explicitly
    /// (<c>DB_MAX_POOL_SIZE</c>), so the app can't open more connections than the Neon compute accepts.
    /// </summary>
    internal static string Normalize(string cs, int maxPoolSize)
    {
        var parsed = new NpgsqlConnectionStringBuilder(cs);
        parsed.Remove("Command Timeout");
        parsed.Remove("Maximum Pool Size");
        // Rebuild in a fixed key order: Remove + re-add reuses the freed slot, so the output order (part of the
        // pool key) would otherwise depend on where the keys were, and one database could get two pools.
        var canonical = new NpgsqlConnectionStringBuilder();
        foreach (var key in parsed.Keys.Cast<string>().Order(StringComparer.OrdinalIgnoreCase))
            canonical[key] = parsed[key];
        canonical.CommandTimeout = ServiceCollectionExtensions.CommandTimeoutSeconds;
        canonical.MaxPoolSize = maxPoolSize;
        return canonical.ConnectionString;
    }

    // A typo or 0 must not lift the cap: anything that isn't a positive integer falls back to the default (as LimitsOptions).
    internal static int MaxPoolSizeFrom(IConfiguration config) =>
        int.TryParse(config["DB_MAX_POOL_SIZE"], out var v) && v > 0 ? v : DefaultMaxPoolSize;

    internal int DistinctDataSources => _byConnectionString.Count;

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _byConnectionString.Values.Where(l => l.IsValueCreated))
            await lazy.Value.DisposeAsync();
    }

    /// <summary>
    /// For containers disposed synchronously (the CLI tools): EF's pooled factory resolves the registry when it is
    /// built, so the registry can be owned by such a container even if no connection was ever opened.
    /// </summary>
    public void Dispose()
    {
        foreach (var lazy in _byConnectionString.Values.Where(l => l.IsValueCreated))
            lazy.Value.Dispose();
    }
}
