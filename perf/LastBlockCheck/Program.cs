// #134 / #43 AC4: "With 18k+ matching rows, scrolling to the last row returns correct data and summary equals an
// independent SQL SUM." Runs against a seeded scale-1.0 stack (the CI budgets job; locally: compose or `dotnet run`).
// Local or CI stack only, never Render, Neon or production:
//   BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… DATABASE_URL=… dotnet run -c Release --project perf/LastBlockCheck
// Prints a markdown table; exits 1 if any check fails. The password is read from the environment and never printed.
// This file does the HTTP and SQL; the comparison and the table are in LastBlockComparison.cs (#181, unit-tested).
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Desk.Data;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using LastBlockCheck;
using Microsoft.Extensions.Configuration;
using Npgsql;

const int Block = GridQueryNormalizer.MaxBlockRows;

var baseUrl = new Uri(Environment.GetEnvironmentVariable("BASE_URL") ?? "http://localhost:8080");
var email = Environment.GetEnvironmentVariable("DESK_EMAIL");
var password = Environment.GetEnvironmentVariable("DESK_PASSWORD");
if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine("Set DESK_EMAIL and DESK_PASSWORD (create the account with Desk.UserAdmin), and DATABASE_URL.");
    return 2;
}
var config = new ConfigurationBuilder().AddEnvironmentVariables().Build();

// A hung API or database fails the check within 3 minutes, not at the job timeout. Ctrl+C cancels too.
using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
var ct = cts.Token;

// Bearer token (ADR-0024): the access token lasts 5 minutes, longer than this check's 3-minute deadline.
using var http = new HttpClient { BaseAddress = baseUrl };
var login = await http.PostAsJsonAsync("/api/auth/login", new { email, password }, ct);
if (!login.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"login failed: HTTP {(int)login.StatusCode}");
    return 2;
}
using (var tokens = JsonDocument.Parse(await login.Content.ReadAsStringAsync(ct)))
    http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", tokens.RootElement.GetProperty("accessToken").GetString());

var columns = BuiltInPresets.Risk.ToArray();
async Task<JsonElement> QueryAsync(int start)
{
    using var req = new HttpRequestMessage(HttpMethod.Post, "/api/positions/query")
    {
        Content = JsonContent.Create(new
        {
            columns, startRow = start, endRow = start + Block,
            sortModel = new[] { new { colId = "market_value", sort = "desc" } },
        }),
    };
    using var res = await http.SendAsync(req, ct);
    res.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
}

// The viewer's entitled portfolios (ADR-0021), so the independent SQL sees the same rows the API may serve.
int[] portfolios;
using (var req = new HttpRequestMessage(HttpMethod.Get, "/api/meta/portfolios"))
{
    using var res = await http.SendAsync(req, ct);
    res.EnsureSuccessStatusCode();
    using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    portfolios = [.. doc.RootElement.EnumerateArray().Select(p => p.GetProperty("portfolioId").GetInt32())];
}

var first = await QueryAsync(0);
var rowCount = first.GetProperty("rowCount").GetInt32();
var asOf = DateOnly.Parse(first.GetProperty("asOf").GetString()!, CultureInfo.InvariantCulture);
// The last full block, ending at the last row (an aligned block can hold a single row: 20,001 rows → 1).
var lastStart = Math.Max(0, rowCount - Block);
var last = await QueryAsync(lastStart);
// position_id is always the first column of a block.
long[] apiIds = [.. last.GetProperty("data")[0].EnumerateArray().Select(v => v.GetInt64())];
// The block AG Grid's infinite model really requests when scrolled to the end: [floor((rowCount-1)/Block)*Block, rowCount),
// an OFFSET at the exact block boundary with endRow past rowCount (1 row at 20,001 rows).
var alignedStart = (rowCount - 1) / Block * Block;
var aligned = await QueryAsync(alignedStart);
long[] alignedIds = [.. aligned.GetProperty("data")[0].EnumerateArray().Select(v => v.GetInt64())];

await using var db = NpgsqlDataSource.Create(ConnectionStrings.Resolve(config, ConnectionStrings.Core));
// DBNull (e.g. sum() over all-NULL values) comes back as null, so it shows as a FAIL row instead of a crash.
async Task<object?> ScalarAsync(string sql)
{
    await using var cmd = db.CreateCommand(sql);
    cmd.Parameters.AddWithValue("asof", asOf);
    cmd.Parameters.AddWithValue("portfolios", portfolios);
    var value = await cmd.ExecuteScalarAsync(ct);
    return value is DBNull ? null : value;
}

// The Risk preset's Sum columns. Names come from the compiled ColumnCatalog allowlist (compile-time constants, never
// client input); the shape guard keeps them safe to quote in SQL.
var sumColumns = ColumnCatalog.PositionSnapshot.Where(c => c.Aggregation == Aggregation.Sum && columns.Contains(c.Name)).ToArray();
foreach (var column in sumColumns)
    if (!Regex.IsMatch(column.Name, "^[a-z][a-z0-9_]*$")) throw new InvalidOperationException(column.Name);

var sqlCount = (long)(await ScalarAsync("SELECT count(*) FROM core.position_snapshot WHERE as_of_date = @asof AND portfolio_id = ANY(@portfolios)"))!;

DateOnly? latest;
await using (var maxCmd = db.CreateCommand("SELECT max(as_of_date) FROM core.position_snapshot"))
await using (var maxReader = await maxCmd.ExecuteReaderAsync(ct))
{
    await maxReader.ReadAsync(ct);
    latest = maxReader.IsDBNull(0) ? null : maxReader.GetFieldValue<DateOnly>(0);
}

// The SQL tail, in the grid's order: the sort key, then position_id in the sort key's direction. It also reads every
// Sum column, so the last block's cells are compared, not only its ids.
var sqlIds = new List<long>();
var sqlCells = new List<decimal?[]>();
var tailColumns = string.Concat(sumColumns.Select(c => $", \"{c.Name}\""));
await using (var cmd = db.CreateCommand($"SELECT position_id{tailColumns} FROM core.position_snapshot WHERE as_of_date = @asof AND portfolio_id = ANY(@portfolios) ORDER BY market_value DESC, position_id DESC OFFSET @start LIMIT @block"))
{
    cmd.Parameters.AddWithValue("asof", asOf);
    cmd.Parameters.AddWithValue("portfolios", portfolios);
    cmd.Parameters.AddWithValue("start", lastStart);
    cmd.Parameters.AddWithValue("block", Block);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        sqlIds.Add(reader.GetInt64(0));
        sqlCells.Add([.. sumColumns.Select((_, i) => reader.IsDBNull(i + 1) ? (decimal?)null : reader.GetDecimal(i + 1))]);
    }
}

// An independent SUM per Sum column, compared with the summary in LastBlockComparison.
var sqlSums = new List<decimal?>();
foreach (var column in sumColumns)
    sqlSums.Add((decimal?)await ScalarAsync($"SELECT sum(\"{column.Name}\") FROM core.position_snapshot WHERE as_of_date = @asof AND portfolio_id = ANY(@portfolios)"));

var results = LastBlockComparison.Compare(
    [.. sumColumns.Select(c => c.Name)],
    new ApiBlocks(rowCount, asOf, first.GetProperty("summary"), lastStart, apiIds, last, alignedStart, alignedIds),
    new SqlFacts(sqlCount, latest, sqlIds, sqlCells, sqlSums));
foreach (var line in LastBlockComparison.Render(rowCount, asOf, portfolios.Length, results))
    Console.WriteLine(line);
return LastBlockComparison.ExitCode(results);
