// #134 / #43 AC4: "With 18k+ matching rows, scrolling to the last row returns correct data and summary equals an
// independent SQL SUM." Runs against a seeded scale-1.0 stack (the CI budgets job; locally: compose or `dotnet run`).
// Local or CI stack only, never Render, Neon or production:
//   BASE_URL=http://localhost:8080 DESK_EMAIL=… DESK_PASSWORD=… DATABASE_URL=… dotnet run -c Release --project perf/LastBlockCheck
// Prints a markdown table; exits 1 if any check fails. The password is read from the environment and never printed.
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Desk.Data;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Microsoft.Extensions.Configuration;
using Npgsql;

const int Block = GridQueryNormalizer.MaxBlockRows;
const int MinRows = 18_000;

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

// Cookies by hand: the session cookie is __Host- (Secure), which a cookie container won't send over plain http.
using var http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = baseUrl };
var login = await http.PostAsJsonAsync("/api/auth/login", new { email, password }, ct);
if (!login.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"login failed: HTTP {(int)login.StatusCode}");
    return 2;
}
var cookies = login.Headers.GetValues("Set-Cookie").Select(c => c.Split(';')[0]).ToDictionary(c => c[..c.IndexOf('=')], c => c[(c.IndexOf('=') + 1)..]);
var cookie = string.Join("; ", cookies.Select(c => $"{c.Key}={c.Value}"));
var xsrf = Uri.UnescapeDataString(cookies["XSRF-TOKEN"]);

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
    req.Headers.Add("Cookie", cookie);
    req.Headers.Add("X-XSRF-TOKEN", xsrf);
    using var res = await http.SendAsync(req, ct);
    res.EnsureSuccessStatusCode();
    return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct)).RootElement.Clone();
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
    var value = await cmd.ExecuteScalarAsync(ct);
    return value is DBNull ? null : value;
}
static string Show(decimal? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "null";
static decimal? Number(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : null;

// The Risk preset's Sum columns. Names come from the compiled ColumnCatalog allowlist (compile-time constants, never
// client input); the shape guard keeps them safe to quote in SQL.
var sumColumns = ColumnCatalog.PositionSnapshot.Where(c => c.Aggregation == Aggregation.Sum && columns.Contains(c.Name)).ToArray();
foreach (var column in sumColumns)
    if (!Regex.IsMatch(column.Name, "^[a-z][a-z0-9_]*$")) throw new InvalidOperationException(column.Name);

var results = new List<(string Check, string Api, string Sql, bool Ok)>();
var sqlCount = (long)(await ScalarAsync("SELECT count(*) FROM core.position_snapshot WHERE as_of_date = @asof"))!;
results.Add(("rows ≥ 18,000", $"{rowCount:N0}", $"{sqlCount:N0}", rowCount >= MinRows && rowCount == sqlCount));

// The checks below reuse the API's as-of; this row makes sure the API's default is the latest snapshot date.
await using (var maxCmd = db.CreateCommand("SELECT max(as_of_date) FROM core.position_snapshot"))
await using (var maxReader = await maxCmd.ExecuteReaderAsync(ct))
{
    await maxReader.ReadAsync(ct);
    var latest = maxReader.IsDBNull(0) ? (DateOnly?)null : maxReader.GetFieldValue<DateOnly>(0);
    results.Add(("as-of is the latest date", $"{asOf:yyyy-MM-dd}", latest?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "null", asOf == latest));
}

// The SQL tail, in the grid's order: the sort key, then position_id in the sort key's direction. It also reads every
// Sum column, so the last block's cells are compared, not only its ids.
var sqlIds = new List<long>();
var sqlCells = new List<decimal?[]>();
var tailColumns = string.Concat(sumColumns.Select(c => $", \"{c.Name}\""));
await using (var cmd = db.CreateCommand($"SELECT position_id{tailColumns} FROM core.position_snapshot WHERE as_of_date = @asof ORDER BY market_value DESC, position_id DESC OFFSET @start LIMIT @block"))
{
    cmd.Parameters.AddWithValue("asof", asOf);
    cmd.Parameters.AddWithValue("start", lastStart);
    cmd.Parameters.AddWithValue("block", Block);
    await using var reader = await cmd.ExecuteReaderAsync(ct);
    while (await reader.ReadAsync(ct))
    {
        sqlIds.Add(reader.GetInt64(0));
        sqlCells.Add([.. sumColumns.Select((_, i) => reader.IsDBNull(i + 1) ? (decimal?)null : reader.GetDecimal(i + 1))]);
    }
}
results.Add(($"last block ids (rows {lastStart + 1:N0}–{rowCount:N0})", $"{apiIds.Length} ids, last {apiIds.LastOrDefault()}",
    $"{sqlIds.Count} ids, last {sqlIds.LastOrDefault()}", apiIds.SequenceEqual(sqlIds) && apiIds.Length == rowCount - lastStart));
// The last block's Sum-column cells, at each column's index in the response's column order. A misaligned column in
// the columnar serializer at a high offset fails here even when the ids match.
var apiColumns = last.GetProperty("columns").EnumerateArray().Select(c => c.GetString()).ToList();
var data = last.GetProperty("data");
int cells = 0, cellMismatches = 0, missingColumns = 0;
for (var j = 0; j < sumColumns.Length; j++)
{
    var index = apiColumns.IndexOf(sumColumns[j].Name);
    if (index < 0) { missingColumns++; continue; }
    var apiValues = data[index].EnumerateArray().Select(Number).ToArray();
    for (var row = 0; row < Math.Max(apiValues.Length, sqlCells.Count); row++)
    {
        cells++;
        var api = row < apiValues.Length ? apiValues[row] : null;
        var sql = row < sqlCells.Count ? sqlCells[row][j] : null;
        if (row >= apiValues.Length || row >= sqlCells.Count || api != sql) cellMismatches++;
    }
}
results.Add(($"last block Sum-column cells ({sumColumns.Length} columns)", $"{cells - cellMismatches:N0} equal",
    $"{cells:N0} cells, {cellMismatches} differ, {missingColumns} columns missing",
    cells > 0 && cellMismatches == 0 && missingColumns == 0));
results.Add(($"aligned last block ids (rows {alignedStart + 1:N0}–{rowCount:N0})", $"{alignedIds.Length} ids, last {alignedIds.LastOrDefault()}",
    $"{rowCount - alignedStart} ids, last {sqlIds.LastOrDefault()}",
    alignedIds.Length == rowCount - alignedStart && alignedIds.SequenceEqual(sqlIds.TakeLast(alignedIds.Length))));

// Every SUM in the summary against an independent SUM.
var summary = first.GetProperty("summary");
foreach (var column in sumColumns)
{
    // A missing key or a JSON null is a FAIL row, not an exception.
    decimal? api = summary.TryGetProperty(column.Name, out var e) && e.ValueKind == JsonValueKind.Number ? e.GetDecimal() : null;
    var sql = (decimal?)await ScalarAsync($"SELECT sum(\"{column.Name}\") FROM core.position_snapshot WHERE as_of_date = @asof");
    results.Add(($"SUM({column.Name})", Show(api), Show(sql), api is not null && api == sql));
}
// The loop above must not pass by comparing nothing: the Sum columns the API served equal the ones compared.
var expected = sumColumns.Select(c => c.Name).ToHashSet();
var served = summary.EnumerateObject().Select(p => p.Name)
    .Where(n => ColumnCatalog.PositionSnapshot.Any(c => c.Name == n && c.Aggregation == Aggregation.Sum)).ToHashSet();
results.Add(("SUM columns compared", $"{served.Count}", $"{expected.Count}", expected.Count > 0 && expected.SetEquals(served)));

Console.WriteLine($"### Last block and summary at {rowCount:N0} rows (as of {asOf:yyyy-MM-dd}, Risk preset, market value desc)");
Console.WriteLine();
Console.WriteLine("| Check | API | Independent SQL | Result |");
Console.WriteLine("|---|---:|---:|---|");
foreach (var (check, api, sql, ok) in results)
    Console.WriteLine($"| {check} | {api} | {sql} | {(ok ? "ok" : "FAIL")} |");
return results.All(r => r.Ok) ? 0 : 1;
