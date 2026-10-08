// Usage (a seeded database; read-only):
//   DATABASE_URL=... dotnet run -c Release --project perf/GridBenchmark -- [iterations=200] [outDir=perf/out]
// Runs the P1 grid query three ways (ADR-0006), serializes the same block three ways (ADR-0007, and writes the
// payloads to outDir for perf/parse-bench.mjs), and times OFFSET vs keyset paging at depth (ADR-0008).
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Desk.Api.Positions;
using Desk.Data;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Desk.Data.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace GridBenchmark;

public static class Bench
{
    private static readonly DateOnly AsOf = new(2026, 10, 6);

    public static async Task Main(string[] args)
    {
        var iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 200;
        var outDir = args.Length > 1 ? args[1] : "perf/out";
        Directory.CreateDirectory(outDir);

        var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
        var cs = ConnectionStrings.Resolve(config, ConnectionStrings.Core);
        await using var registry = new DataSourceRegistry(config, new DbConnectionCounter());
        var repo = new GridRepository(registry);
        var catalog = ColumnCatalog.PositionSnapshot;
        var normalizer = new GridQueryNormalizer(catalog);
        int[] portfolios;
        await using (var conn = new NpgsqlConnection(cs))
            portfolios = (await conn.QueryAsync<int>("SELECT portfolio_id FROM core.portfolio")).ToArray();

        Console.WriteLine($"iterations={iterations}  as_of={AsOf:yyyy-MM-dd}  .NET {Environment.Version}  {Environment.ProcessorCount} cores");
        foreach (var (preset, columns) in new[] { ("Risk", BuiltInPresets.Risk), ("All", BuiltInPresets.All) })
        {
            var request = new GridRequest(Columns: [.. columns], SortModel: [new("market_value", "desc")], StartRow: 0, EndRow: 200);
            var query = normalizer.Normalize(request, AsOf, portfolios);
            Console.WriteLine($"\n## {preset} preset: first block, 200 rows x {query.Columns.Count} columns, sort market_value desc");

            var known = new GridSummary(0, []);
            Console.WriteLine("\nADR-0006 access path: the page (200 rows)");
            Console.WriteLine("| Path | p50 ms | p95 ms | alloc KB/op |");
            Console.WriteLine("|---|---:|---:|---:|");
            await Measure("Dapper + typed columnar reader (chosen)", iterations, () => repo.ReadBlockAsync(query, catalog, CancellationToken.None, known));
            await Measure("Dapper Query<dynamic> + transpose", iterations, () => DapperDynamicAsync(cs, query, catalog));
            await Measure("EF Core property bag (all columns)", iterations, () => EfPageAsync(cs, query));
            Console.WriteLine("\nSummary (COUNT + aggregates over every filtered row), same SQL for every path");
            Console.WriteLine("| Batch | p50 ms | p95 ms | alloc KB/op |");
            Console.WriteLine("|---|---:|---:|---:|");
            await Measure("page + summary, one round trip (first block of a view)", Math.Max(20, iterations / 4), () => repo.ReadBlockAsync(query, catalog, CancellationToken.None));

            var block = await repo.ReadBlockAsync(query, catalog, CancellationToken.None);
            var generatedAt = DateTimeOffset.UtcNow;
            Console.WriteLine("\nADR-0007 serialization of that block");
            Console.WriteLine("| Format | raw KB | gzip KB | brotli KB | serialize ms (p50) |");
            Console.WriteLine("|---|---:|---:|---:|---:|");
            Serialize($"{preset}-rows.json", "Row JSON (array of objects)", outDir, iterations, () => RowJson(block));
            Serialize($"{preset}-columnar.json", "Columnar JSON (chosen)", outDir, iterations, () => ColumnarSerializer.ToJson(block, AsOf, generatedAt));
            Serialize($"{preset}-columnar.msgpack", "Columnar MessagePack", outDir, iterations, () => ColumnarSerializer.ToMsgPack(block, AsOf, generatedAt));
        }

        Console.WriteLine("\n## ADR-0008 paging depth: 200 rows x Risk columns, sort market_value desc (page query only)");
        Console.WriteLine("| Start row | OFFSET p50 ms | keyset p50 ms |");
        Console.WriteLine("|---:|---:|---:|");
        await PagingAsync(cs, portfolios, iterations);
    }

    private static async Task Measure(string name, int iterations, Func<Task> run)
    {
        for (var i = 0; i < 10; i++) await run(); // warm-up: pool, plans, JIT
        var ms = new double[iterations];
        var allocated = GC.GetTotalAllocatedBytes(true);
        for (var i = 0; i < iterations; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await run();
            ms[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        var perOp = (GC.GetTotalAllocatedBytes(true) - allocated) / 1024.0 / iterations;
        Array.Sort(ms);
        Console.WriteLine($"| {name} | {Pct(ms, 50):0.0} | {Pct(ms, 95):0.0} | {perOp:0} |");
    }

    private static void Serialize(string file, string name, string outDir, int iterations, Func<byte[]> serialize)
    {
        var bytes = serialize();
        for (var i = 0; i < 10; i++) serialize();
        var ms = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var t = Stopwatch.GetTimestamp();
            serialize();
            ms[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        Array.Sort(ms);
        File.WriteAllBytes(Path.Combine(outDir, file), bytes);
        Console.WriteLine($"| {name} | {bytes.Length / 1024.0:0.0} | {Compressed(bytes, s => new GZipStream(s, CompressionLevel.Fastest)) / 1024.0:0.0} | " +
                          $"{Compressed(bytes, s => new BrotliStream(s, CompressionLevel.Fastest)) / 1024.0:0.0} | {Pct(ms, 50):0.00} |");
    }

    /// <summary>Compressed size at the API's level (Fastest, Program.cs).</summary>
    private static long Compressed(byte[] bytes, Func<Stream, Stream> wrap)
    {
        using var output = new MemoryStream();
        using (var z = wrap(output)) z.Write(bytes);
        return output.ToArray().Length; // the compressor disposes the stream; ToArray works after Dispose
    }

    private static double Pct(double[] sorted, int p) => sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(p / 100.0 * sorted.Length) - 1)];

    /// <summary>The classic row DTO: one object per row, property names repeated in every row.</summary>
    private static byte[] RowJson(GridBlock block)
    {
        var rows = new List<Dictionary<string, object?>>(block.Rows);
        for (var r = 0; r < block.Rows; r++)
        {
            var row = new Dictionary<string, object?>(block.Columns.Count);
            foreach (var c in block.Columns)
                row[c.Def.Name] = c switch
                {
                    Int64Column x => x.Values[r], Int32Column x => x.Values[r], DecimalColumn x => x.Values[r],
                    DoubleColumn x => x.Values[r], BoolColumn x => x.Values[r], DateColumn x => x.Values[r],
                    TextColumn x => x.Values[r], _ => null,
                };
            rows.Add(row);
        }
        return JsonSerializer.SerializeToUtf8Bytes(new { rows, rowCount = block.RowCount, summary = block.Summary.ToDictionary(s => s.Column.Name, s => s.Value) });
    }

    private static async Task DapperDynamicAsync(string cs, GridQuery query, IReadOnlyCollection<ColumnDef> catalog)
    {
        var sql = GridSqlBuilder.Build(query, catalog, includeSummary: false);
        var p = new DynamicParameters();
        foreach (var (k, v) in sql.Parameters) p.Add(k, v is DateOnly d ? d.ToDateTime(TimeOnly.MinValue) : v);
        await using var conn = new NpgsqlConnection(cs);
        var rows = (await conn.QueryAsync(sql.Sql.Replace("@as_of", "@as_of::date"), p)).Cast<IDictionary<string, object?>>().ToList();
        var columns = query.Columns.Select(c => rows.Select(r => r[c.Name]).ToArray()).ToArray(); // transpose (boxed)
        GC.KeepAlive(columns);
    }

    private static async Task EfPageAsync(string cs, GridQuery query)
    {
        await using var db = new SnapshotContext(cs);
        var page = await db.Set<Dictionary<string, object>>("snapshot").AsNoTracking()
            .Where(e => EF.Property<DateOnly>(e, "as_of_date") == query.AsOf && query.PortfolioIds.Contains(EF.Property<int>(e, "portfolio_id")))
            .OrderByDescending(e => EF.Property<decimal?>(e, "market_value")).ThenByDescending(e => EF.Property<long>(e, "position_id"))
            .Skip(query.Offset).Take(query.Limit)
            .ToListAsync();
        // EF can't project a column set chosen at runtime without expression building; it materializes every column.
        var columns = query.Columns.Select(c => page.Select(r => r.GetValueOrDefault(c.Name)).ToArray()).ToArray();
        GC.KeepAlive(columns);
    }

    private static async Task PagingAsync(string cs, int[] portfolios, int iterations)
    {
        var cols = string.Join(", ", new[] { "position_id" }.Concat(BuiltInPresets.Risk).Select(c => $"\"{c}\""));
        const string where = "as_of_date = @as_of AND portfolio_id = ANY(@portfolios)";
        await using var conn = new NpgsqlConnection(cs);
        await conn.OpenAsync();
        var total = await conn.ExecuteScalarAsync<int>($"SELECT count(*) FROM core.position_snapshot WHERE {where}", new { as_of = AsOf.ToDateTime(TimeOnly.MinValue), portfolios });
        foreach (var start in new[] { 0, 2_000, 10_000, total - 200 })
        {
            var offsetSql = $"SELECT {cols} FROM core.position_snapshot WHERE {where} ORDER BY market_value DESC, position_id DESC OFFSET {start} LIMIT 200";
            // Keyset needs the previous block's last key: available when scrolling block by block, not after a jump.
            var boundary = await conn.QuerySingleOrDefaultAsync<(decimal Mv, long Id)?>(
                $"SELECT market_value, position_id FROM core.position_snapshot WHERE {where} ORDER BY market_value DESC, position_id DESC OFFSET {Math.Max(0, start - 1)} LIMIT 1",
                new { as_of = AsOf.ToDateTime(TimeOnly.MinValue), portfolios });
            var keysetSql = start == 0
                ? $"SELECT {cols} FROM core.position_snapshot WHERE {where} ORDER BY market_value DESC, position_id DESC LIMIT 200"
                : $"SELECT {cols} FROM core.position_snapshot WHERE {where} AND (market_value, position_id) < (@mv, @id) ORDER BY market_value DESC, position_id DESC LIMIT 200";
            var parameters = new { as_of = AsOf.ToDateTime(TimeOnly.MinValue), portfolios, mv = boundary?.Mv ?? 0, id = boundary?.Id ?? 0 };
            var offsetMs = await Time(conn, offsetSql, parameters, iterations);
            var keysetMs = await Time(conn, keysetSql, parameters, iterations);
            Console.WriteLine($"| {start:N0} | {offsetMs:0.00} | {keysetMs:0.00} |");
        }
    }

    private static async Task<double> Time(NpgsqlConnection conn, string sql, object parameters, int iterations)
    {
        for (var i = 0; i < 5; i++) await conn.QueryAsync(sql.Replace("@as_of", "@as_of::date"), parameters);
        var ms = new double[iterations];
        for (var i = 0; i < iterations; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await conn.QueryAsync(sql.Replace("@as_of", "@as_of::date"), parameters);
            ms[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        Array.Sort(ms);
        return Pct(ms, 50);
    }

    /// <summary>EF Core over the 202-column snapshot as a shared-type property bag, built from the catalog.</summary>
    private sealed class SnapshotContext(string cs) : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder o) => o.UseNpgsql(cs);

        protected override void OnModelCreating(ModelBuilder b) => b.SharedTypeEntity<Dictionary<string, object>>("snapshot", e =>
        {
            e.ToTable("position_snapshot", "core");
            foreach (var c in ColumnCatalog.PositionSnapshot)
                e.IndexerProperty(ClrType(c), c.Name);
            e.HasKey("as_of_date", "position_id");
        });

        private static Type ClrType(ColumnDef c) => c.Kind switch
        {
            ColumnKind.Key when c.Name == "position_id" => typeof(long),
            ColumnKind.Key => typeof(int),
            ColumnKind.Count => typeof(int?),
            ColumnKind.Text => typeof(string),
            ColumnKind.Date => c.Name == "as_of_date" ? typeof(DateOnly) : typeof(DateOnly?),
            ColumnKind.Money => typeof(decimal?),
            ColumnKind.Flag => typeof(bool?),
            _ => typeof(double?),
        };
    }
}
