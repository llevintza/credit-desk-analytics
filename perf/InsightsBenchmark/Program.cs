// Usage (a seeded database; read-only). Local stack only (never Render/Neon/production):
//   DATABASE_URL=... dotnet run -c Release --project perf/InsightsBenchmark -- [iterations=30] [asOf=latest]
// ADR-0012: four ways to load the 20 P3 grids, all uncached (every grid hits the database), all portfolios.
// "First" is when the first grid could paint, "all" when the last one could; a response paints only when it is
// complete, so a single call paints nothing until all 20 grids are done.
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Dapper;
using Desk.Data;
using Desk.Data.Insights;
using Desk.Data.Sources;
using Microsoft.Extensions.Configuration;

var iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 30;
var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();
var counter = new DbConnectionCounter();
await using var sources = new DataSourceRegistry(config, counter);
var options = new InsightsOptions(InsightsOptions.DefaultPerRequest, InsightsOptions.DefaultGlobal);

DateOnly asOf;
int[] portfolios;
await using (var conn = await sources.OpenAsync(ConnectionStrings.Core, CancellationToken.None))
{
    asOf = args.Length > 1 ? DateOnly.Parse(args[1], CultureInfo.InvariantCulture) : await conn.QuerySingleAsync<DateOnly>("SELECT max(as_of_date) FROM core.position_snapshot");
    portfolios = [.. await conn.QueryAsync<int>("SELECT portfolio_id FROM core.portfolio ORDER BY 1")];
}
var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
var ct = CancellationToken.None;
Console.WriteLine($"iterations={iterations}  asOf={asOf:yyyy-MM-dd}  portfolios={portfolios.Length}  grids={InsightCatalog.All.Count}  .NET {Environment.Version}  per-request cap={options.PerRequest} global cap={options.Global}");

// Every grid once, so the plans are cached and the pages are warm, and a correctness check: no grid is empty.
using (var repo = new InsightsRepository(sources, options))
{
    var grids = await repo.ReadAsync(InsightCatalog.All, asOf, portfolios, ct);
    if (grids.Any(g => g.Rows.Length == 0)) throw new InvalidOperationException("a grid is empty on the full book");
    // Each grid alone on one connection: where the time goes.
    await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
    Console.WriteLine("| Source | Grid | rows × cols | p50 ms alone |");
    Console.WriteLine("|---|---|---:|---:|");
    foreach (var (spec, g) in InsightCatalog.All.Zip(grids))
    {
        var ms = new double[Math.Max(5, iterations)];
        for (var i = 0; i < ms.Length; i++)
        {
            var t = Stopwatch.GetTimestamp();
            await InsightsRepository.RunAsync(conn, spec, asOf, portfolios, ct);
            ms[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        Array.Sort(ms);
        Console.WriteLine($"| {spec.Source} | {g.Id} | {g.Rows.Length} × {g.Columns.Length} | {P(ms, 0.5):0.0} |");
    }
}

// A. One call, one connection (what one shared DbContext forces: a single command in flight at a time).
async Task<(double First, double All)> OneCallSerial()
{
    var t = Stopwatch.GetTimestamp();
    await using var conn = await sources.OpenAsync(ConnectionStrings.Core, ct);
    foreach (var spec in InsightCatalog.All) await InsightsRepository.RunAsync(conn, spec, asOf, portfolios, ct);
    var ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    return (ms, ms);
}

// B. One call, per-task connections (capped like a request): all grids parallel, still one response.
async Task<(double First, double All)> OneCallParallel()
{
    var t = Stopwatch.GetTimestamp();
    using var repo = new InsightsRepository(sources, options);
    await repo.ReadAsync(InsightCatalog.All, asOf, portfolios, ct);
    var ms = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    return (ms, ms);
}

// C. Five per-source calls in parallel (the chosen design): each its own fan-out, all under one global cap.
async Task<(double First, double All)> PerSource()
{
    var t = Stopwatch.GetTimestamp();
    using var repo = new InsightsRepository(sources, options);
    var done = await Task.WhenAll(InsightCatalog.Sources.Select(async s =>
    {
        await repo.ReadAsync(InsightCatalog.For(s), asOf, portfolios, ct);
        return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }));
    return (done.Min(), done.Max());
}

// D. 20 calls, one per grid, at most 6 at a time (a browser's HTTP/1.1 limit per origin); each its own connection.
async Task<(double First, double All)> TwentyCalls()
{
    var t = Stopwatch.GetTimestamp();
    using var browser = new SemaphoreSlim(6);
    var done = await Task.WhenAll(InsightCatalog.All.Select(async spec =>
    {
        await browser.WaitAsync(ct);
        try
        {
            await using var conn = await sources.OpenAsync(InsightCatalog.ConnectionName(spec.Source), ct);
            await InsightsRepository.RunAsync(conn, spec, asOf, portfolios, ct);
            return Stopwatch.GetElapsedTime(t).TotalMilliseconds;
        }
        finally
        {
            browser.Release();
        }
    }));
    return (done.Min(), done.Max());
}

// Payload per response (JSON; brotli as the API sends it).
async Task<(int Json, int Br)[]> Payloads(IEnumerable<IReadOnlyList<InsightSpec>> responses)
{
    using var repo = new InsightsRepository(sources, options);
    var sizes = new List<(int, int)>();
    foreach (var specs in responses)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new InsightsResult("x", asOf, await repo.ReadAsync(specs, asOf, portfolios, ct)), json);
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) br.Write(bytes);
        sizes.Add((bytes.Length, (int)ms.Length));
    }
    return [.. sizes];
}

var one = await Payloads([InsightCatalog.All]);
var perSource = await Payloads(InsightCatalog.Sources.Select(InsightCatalog.For));
var perGrid = await Payloads(InsightCatalog.All.Select(s => (IReadOnlyList<InsightSpec>)[s]));

// Interleaved: each iteration runs every approach once, in a rotating order, so load drift on the machine (or the
// database's caches) lands on all four alike instead of on whichever ran last.
(string Name, Func<Task<(double First, double All)>> Run, int Requests, (int Json, int Br)[] Sizes)[] approaches =
[
    ("A. One call, one connection (serial)", OneCallSerial, 1, one),
    ("B. One call, per-task connections", OneCallParallel, 1, one),
    ("C. Per-source calls, per-task connections (chosen)", PerSource, 5, perSource),
    ("D. 20 calls, one per grid (6 at a time)", TwentyCalls, 20, perGrid),
];
foreach (var a in approaches) for (var i = 0; i < 3; i++) await a.Run();
var first = approaches.Select(_ => new double[iterations]).ToArray();
var all = approaches.Select(_ => new double[iterations]).ToArray();
var opened = new long[approaches.Length];
for (var i = 0; i < iterations; i++)
    for (var k = 0; k < approaches.Length; k++)
    {
        var j = (i + k) % approaches.Length;
        var before = counter.Opened;
        (first[j][i], all[j][i]) = await approaches[j].Run();
        opened[j] += counter.Opened - before;
    }

Console.WriteLine();
Console.WriteLine("| Approach | requests | first grid p50 / p95 ms | all grids p50 / p95 ms | connections per load | largest response JSON / br bytes | total br bytes |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|---:|");
for (var j = 0; j < approaches.Length; j++)
{
    Array.Sort(first[j]);
    Array.Sort(all[j]);
    var (name, _, requests, sizes) = approaches[j];
    var largest = sizes.MaxBy(s => s.Json);
    Console.WriteLine($"| {name} | {requests} | {P(first[j], 0.5):0.0} / {P(first[j], 0.95):0.0} | {P(all[j], 0.5):0.0} / {P(all[j], 0.95):0.0} | {opened[j] / (double)iterations:0} | {largest.Json} / {largest.Br} | {sizes.Sum(s => s.Br)} |");
}

static double P(double[] sorted, double q) => sorted[Math.Min(sorted.Length - 1, (int)(sorted.Length * q))];
