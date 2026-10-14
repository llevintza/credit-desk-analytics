using Desk.Data.Sources;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Desk.Data.Insights;

/// <summary>
/// P3 fan-out limits (README §6 P3, ADR-0012): grids of one request run at most <see cref="PerRequest"/> at a time,
/// and all requests together hold at most <see cref="Global"/> connections, so five sources × four grids can't drain
/// the pool the other endpoints share (<c>DB_MAX_POOL_SIZE</c>, default 20).
/// </summary>
public sealed record InsightsOptions(int PerRequest, int Global)
{
    public const int DefaultPerRequest = 4;
    public const int DefaultGlobal = 10;

    /// <summary><c>INSIGHTS_PARALLELISM</c> (1–16) and <c>INSIGHTS_MAX_CONNECTIONS</c> (1–100); anything else keeps the default.</summary>
    public static InsightsOptions From(IConfiguration config) => new(
        int.TryParse(config["INSIGHTS_PARALLELISM"], out var p) && p is > 0 and <= 16 ? p : DefaultPerRequest,
        int.TryParse(config["INSIGHTS_MAX_CONNECTIONS"], out var g) && g is > 0 and <= 100 ? g : DefaultGlobal);
}

/// <summary>
/// Reads one source's P3 grids. Each grid is its own task on <b>its own pooled connection</b> (never one connection
/// or DbContext shared across concurrent tasks: Npgsql and EF both refuse a second command in flight, see
/// <c>InsightsConcurrencyTests</c>).
/// </summary>
public sealed class InsightsRepository(IDataSourceRegistry sources, InsightsOptions options) : IDisposable
{
    private readonly SemaphoreSlim _global = new(options.Global, options.Global);

    public InsightsOptions Options => options;

    /// <summary>The grids of <paramref name="specs"/>, in their order. An empty scope reads nothing: every grid is empty.</summary>
    public async Task<InsightGrid[]> ReadAsync(IReadOnlyList<InsightSpec> specs, DateOnly asOf, int[] portfolios, CancellationToken ct)
    {
        if (portfolios.Length == 0)
            return [.. specs.Select(s => Shape(s, []))];

        using var perRequest = new SemaphoreSlim(options.PerRequest, options.PerRequest);
        var tasks = specs.Select(async spec =>
        {
            await perRequest.WaitAsync(ct);
            try
            {
                await _global.WaitAsync(ct);
                try
                {
                    await using var conn = await sources.OpenAsync(InsightCatalog.ConnectionName(spec.Source), ct);
                    return await RunAsync(conn, spec, asOf, portfolios, ct);
                }
                finally
                {
                    _global.Release();
                }
            }
            finally
            {
                perRequest.Release();
            }
        });
        return await Task.WhenAll(tasks);
    }

    /// <summary>One grid on the given connection. Public for the concurrency tests and the ADR-0012 benchmark.</summary>
    public static async Task<InsightGrid> RunAsync(NpgsqlConnection conn, InsightSpec spec, DateOnly asOf, int[] portfolios, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(spec.Sql, conn);
        cmd.Parameters.Add(new NpgsqlParameter<DateOnly>("asOf", asOf));
        cmd.Parameters.Add(new NpgsqlParameter<int[]>("portfolios", portfolios));
        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
                row[i] = InsightPivot.Cell(reader.GetValue(i));
            rows.Add(row);
        }
        return Shape(spec, rows);
    }

    /// <summary>Raw rows → the grid (pivoted when the spec is a cross-tab), checked against its headers.</summary>
    public static InsightGrid Shape(InsightSpec spec, IReadOnlyList<object?[]> rows)
    {
        string[] columns;
        object?[][] cells;
        Dictionary<string, string> format;
        if (spec.Pivot is { } pivot)
        {
            (columns, cells) = InsightPivot.CrossTab(spec.Columns[0], pivot, rows);
            format = columns.Skip(1).ToDictionary(c => c, _ => spec.ValueFormat);
            format[columns[0]] = spec.Formats[0];
        }
        else
        {
            columns = spec.Columns;
            cells = [.. rows];
            format = columns.Zip(spec.Formats).ToDictionary(x => x.First, x => x.Second);
        }
        var grid = new InsightGrid(spec.Id, spec.Title, columns, cells, format);
        InsightPivot.Validate(grid);
        return grid;
    }

    public void Dispose() => _global.Dispose();
}
