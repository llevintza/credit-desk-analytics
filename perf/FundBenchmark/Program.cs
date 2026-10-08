// Usage (a seeded database; read-only):
//   DATABASE_URL=... dotnet run -c Release --project perf/FundBenchmark -- [iterations=500]
// ADR-0011: three ways to turn core.fund_performance (long: one row per month-end) into the P2 shape
// (months + one array per measure), for every fund's ITD range.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Dapper;
using Desk.Data;
using Desk.Data.Funds;
using Microsoft.Extensions.Configuration;
using Npgsql;

var iterations = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 500;
var cs = ConnectionStrings.Resolve(new ConfigurationBuilder().AddEnvironmentVariables().Build(), ConnectionStrings.Core);
await using var conn = new NpgsqlConnection(cs);
await conn.OpenAsync();
var funds = (await conn.QueryAsync<FundSpan>(
    "SELECT f.fund_id AS FundId, f.name AS Name, min(as_of_month) AS First, max(as_of_month) AS Last FROM core.fund f JOIN core.fund_performance p USING (fund_id) GROUP BY 1, 2 ORDER BY 1")).ToArray();
Console.WriteLine($"iterations={iterations}  funds={funds.Length}  months/fund={string.Join(',', funds.Select(f => (f.Last.Year - f.First.Year) * 12 + f.Last.Month - f.First.Month + 1))}  .NET {Environment.Version}");

// 1. Long rows + pivot in C# (what the API does): fixed SQL text, one plan.
async Task<int> LongPlusEdgePivot(FundSpan f)
{
    var months = (await conn.QueryAsync<FundMonth>(
        "SELECT as_of_month AS AsOfMonth, balance AS Balance, irr_itd AS IrrItd FROM core.fund_performance WHERE fund_id = @id AND as_of_month BETWEEN @from AND @to ORDER BY as_of_month",
        new { id = f.FundId, from = f.First, to = f.Last })).ToArray();
    return JsonSerializer.SerializeToUtf8Bytes(FundPivot.Pivot(f, "ITD", f.First, f.Last, months)).Length;
}

// 2. SQL builds the arrays (array_agg); C# only maps them: fixed SQL text, the pivot moves into the database.
async Task<int> SqlArrayAgg(FundSpan f)
{
    await using var cmd = new NpgsqlCommand(
        "SELECT array_agg(as_of_month ORDER BY as_of_month), array_agg(balance ORDER BY as_of_month), array_agg(irr_itd ORDER BY as_of_month) FROM core.fund_performance WHERE fund_id = $1 AND as_of_month BETWEEN $2 AND $3",
        conn);
    cmd.Parameters.Add(new() { Value = f.FundId });
    cmd.Parameters.Add(new() { Value = f.First });
    cmd.Parameters.Add(new() { Value = f.Last });
    await using var reader = await cmd.ExecuteReaderAsync();
    await reader.ReadAsync();
    var r = (Months: reader.GetFieldValue<DateOnly[]>(0), Balance: reader.GetFieldValue<decimal[]>(1), Irr: reader.GetFieldValue<double[]>(2));
    var p = new FundPerformance(f.FundId, f.Name, "ITD", f.First, f.Last, [.. r.Months.Select(m => m.ToString("yyyy-MM-dd"))],
        [new FundRow("Balance", "money0", [.. r.Balance.Select(b => (decimal?)b)]), new FundRow("IRR", "pct2", [.. r.Irr.Select(i => (decimal?)i)])]);
    return JsonSerializer.SerializeToUtf8Bytes(p).Length;
}

// 3. A SQL pivot: one column per month (FILTER), generated from the months list: a different SQL text per range.
async Task<int> DynamicSqlPivot(FundSpan f)
{
    var months = new List<DateOnly>();
    for (var m = f.First; m <= f.Last; m = PerformanceRange.MonthEnd(m.AddDays(1))) months.Add(m);
    var sql = new StringBuilder("SELECT 'Balance' AS label");
    foreach (var m in months) sql.Append($", max(balance) FILTER (WHERE as_of_month = '{m:yyyy-MM-dd}')");
    sql.Append(" FROM core.fund_performance WHERE fund_id = @id UNION ALL SELECT 'IRR'");
    foreach (var m in months) sql.Append($", (max(irr_itd) FILTER (WHERE as_of_month = '{m:yyyy-MM-dd}'))::numeric");
    sql.Append(" FROM core.fund_performance WHERE fund_id = @id");
    var rows = (await conn.QueryAsync(sql.ToString(), new { id = f.FundId })).Cast<IDictionary<string, object?>>().ToArray();
    var p = new FundPerformance(f.FundId, f.Name, "ITD", f.First, f.Last, [.. months.Select(m => m.ToString("yyyy-MM-dd"))],
        [.. rows.Select(r => new FundRow((string)r["label"]!, "x", [.. r.Values.Skip(1).Select(v => v is null ? (decimal?)null : Convert.ToDecimal(v, CultureInfo.InvariantCulture))]))]);
    return JsonSerializer.SerializeToUtf8Bytes(p).Length;
}

Console.WriteLine("| Approach | p50 ms (all funds, ITD) | p95 ms | alloc KB/op | payload bytes (fund 1) | distinct SQL texts |");
Console.WriteLine("|---|---:|---:|---:|---:|---:|");
await Measure("Long rows + pivot in C# (chosen)", LongPlusEdgePivot, "1");
await Measure("SQL array_agg", SqlArrayAgg, "1");
await Measure("Dynamic SQL pivot (FILTER per month)", DynamicSqlPivot, "one per range");

async Task Measure(string name, Func<FundSpan, Task<int>> run, string texts)
{
    for (var i = 0; i < 20; i++) foreach (var f in funds) await run(f);
    var ms = new double[iterations];
    var bytes = 0;
    var alloc = GC.GetTotalAllocatedBytes(true);
    for (var i = 0; i < iterations; i++)
    {
        var t = Stopwatch.GetTimestamp();
        foreach (var f in funds) bytes = f.FundId == 1 ? await run(f) : bytes + 0 * await run(f);
        ms[i] = Stopwatch.GetElapsedTime(t).TotalMilliseconds;
    }
    var perOp = (GC.GetTotalAllocatedBytes(true) - alloc) / 1024.0 / iterations;
    Array.Sort(ms);
    Console.WriteLine($"| {name} | {ms[iterations / 2]:0.00} | {ms[(int)(iterations * 0.95)]:0.00} | {perOp:0} | {bytes} | {texts} |");
}
