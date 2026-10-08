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

public sealed class DataSourceRegistry(IConfiguration config, DbConnectionCounter counter) : IDataSourceRegistry, IAsyncDisposable
{
    // Keyed by the normalized connection string, so two sources pointing at one database share a pool.
    private readonly ConcurrentDictionary<string, Lazy<NpgsqlDataSource>> _byConnectionString = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sourceToConnectionString = new(StringComparer.Ordinal);

    public NpgsqlDataSource Get(string source)
    {
        // Resolved on first use, not at startup: the app boots (and /health answers) without a database.
        var cs = _sourceToConnectionString.GetOrAdd(source, s => WithCommandTimeout(ConnectionStrings.Resolve(config, s)));
        return _byConnectionString.GetOrAdd(cs, c => new Lazy<NpgsqlDataSource>(() => NpgsqlDataSource.Create(c))).Value;
    }

    public async ValueTask<NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
    {
        var conn = await Get(source).OpenConnectionAsync(ct);
        counter.Record();
        return conn;
    }

    /// <summary>README §7.2: no data query runs longer than 10 s.</summary>
    internal static string WithCommandTimeout(string cs) =>
        new NpgsqlConnectionStringBuilder(cs) { CommandTimeout = ServiceCollectionExtensions.CommandTimeoutSeconds }.ConnectionString;

    internal int DistinctDataSources => _byConnectionString.Count;

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _byConnectionString.Values.Where(l => l.IsValueCreated))
            await lazy.Value.DisposeAsync();
    }
}
