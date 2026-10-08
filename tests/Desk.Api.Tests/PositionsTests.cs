using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.Positions;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>README §6 P1 acceptance criteria and §11 positions tests, on the seeded (scale 0.1) database.</summary>
[Collection(ApiCollection.Name)]
public sealed class PositionsTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] RiskColumns = [.. BuiltInPresets.Risk];

    private static HttpRequestMessage Query(string xsrf, object body, string? accept = null, string path = "/api/positions/query")
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-XSRF-TOKEN", xsrf);
        if (accept is not null) req.Headers.Add("Accept", accept);
        return req;
    }

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Fact]
    public async Task First_block_is_columnar_with_row_count_and_a_summary_that_matches_independent_sql()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var doc = await ReadJson(await client.SendAsync(Query(xsrf, new
        {
            columns = new[] { "deal_name", "market_value", "dv01", "spread_bp" },
            sortModel = new[] { new { colId = "market_value", sort = "desc" } },
            filterModel = new { sector = new { filterType = "set", values = new[] { "CLO", "RMBS" } } },
        }), Ct));

        Assert.Equal(["position_id", "deal_name", "market_value", "dv01", "spread_bp"], doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        var data = doc.GetProperty("data");
        Assert.Equal(5, data.GetArrayLength());
        Assert.All(data.EnumerateArray(), col => Assert.Equal(200, col.GetArrayLength()));
        Assert.Equal("2026-10-06", doc.GetProperty("asOf").GetString());

        const string where = "as_of_date = DATE '2026-10-06' AND sector IN ('CLO', 'RMBS')";
        Assert.Equal((int)await ScalarAsync<long>($"SELECT count(*) FROM core.position_snapshot WHERE {where}"), doc.GetProperty("rowCount").GetInt32());

        var summary = doc.GetProperty("summary");
        Assert.Equal(await ScalarAsync<decimal>($"SELECT sum(market_value) FROM core.position_snapshot WHERE {where}"), summary.GetProperty("market_value").GetDecimal());
        Assert.Equal(await ScalarAsync<decimal>($"SELECT sum(dv01) FROM core.position_snapshot WHERE {where}"), summary.GetProperty("dv01").GetDecimal());
        var wavg = await ScalarAsync<double>($"SELECT sum(spread_bp * market_value::float8) / sum(market_value::float8) FROM core.position_snapshot WHERE {where}");
        Assert.Equal(wavg, summary.GetProperty("spread_bp").GetDouble(), 6);
        Assert.False(summary.TryGetProperty("deal_name", out _)); // text columns have no aggregate

        // Sorted as asked: market value never increases down the block.
        var mv = data[2].EnumerateArray().Select(v => v.GetDecimal()).ToArray();
        Assert.True(mv.Zip(mv.Skip(1)).All(p => p.First >= p.Second));
    }

    [Theory]
    [InlineData("market_value", "desc")]
    [InlineData("sector", "asc")]
    [InlineData("vintage", "desc")]
    public async Task Concatenating_every_block_gives_each_position_exactly_once_under_any_sort(string column, string direction)
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var ids = new List<long>();
        var total = int.MaxValue;
        for (var start = 0; start < total; start += 500)
        {
            var doc = await ReadJson(await client.SendAsync(Query(xsrf, new
            {
                startRow = start, endRow = start + 500, columns = new[] { column },
                sortModel = new[] { new { colId = column, sort = direction } },
            }), Ct));
            total = doc.GetProperty("rowCount").GetInt32();
            ids.AddRange(doc.GetProperty("data")[0].EnumerateArray().Select(v => v.GetInt64()));
        }

        Assert.Equal(await ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot WHERE as_of_date = DATE '2026-10-06'"), ids.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());
    }

    [Fact]
    public async Task The_last_block_is_short_and_matches_the_tail_in_sql()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var first = await ReadJson(await client.SendAsync(Query(xsrf, new { columns = new[] { "deal_name" }, endRow = 1 }), Ct));
        var rowCount = first.GetProperty("rowCount").GetInt32();

        var last = await ReadJson(await client.SendAsync(Query(xsrf, new { columns = new[] { "deal_name" }, startRow = rowCount - 7, endRow = rowCount + 193 }), Ct));
        var ids = last.GetProperty("data")[0].EnumerateArray().Select(v => v.GetInt64()).ToArray();
        Assert.Equal(7, ids.Length);
        Assert.Equal(await ScalarAsync<long>("SELECT max(position_id) FROM core.position_snapshot WHERE as_of_date = DATE '2026-10-06'"), ids[^1]);
    }

    [Fact]
    public async Task Injection_attempts_in_columns_sort_and_filter_keys_are_dropped_without_a_500()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        const string evil = "deal_name\"; DROP TABLE core.deal; --";
        var res = await client.SendAsync(Query(xsrf, new Dictionary<string, object>
        {
            ["columns"] = new[] { evil, "1; SELECT pg_sleep(10)", "deal_name" },
            ["sortModel"] = new[] { new { colId = evil, sort = "desc" }, new { colId = "market_value", sort = "desc; DROP TABLE x" } },
            ["filterModel"] = new Dictionary<string, object>
            {
                [evil] = new { filterType = "text", type = "contains", filter = "x" },
                ["deal_name"] = new { filterType = "text", type = "contains", filter = "'; DROP TABLE core.deal; --" },
            },
            ["quickFilter"] = "%' OR 1=1 --",
        }), Ct);
        var doc = await ReadJson(res);
        Assert.Equal(["position_id", "deal_name"], doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        Assert.Equal(0, doc.GetProperty("rowCount").GetInt32()); // the literal text matches nothing
        Assert.True(await ScalarAsync<long>("SELECT count(*) FROM core.deal") > 0);
    }

    [Fact]
    public async Task Repeat_is_a_cache_hit_and_if_none_match_is_304()
    {
        var (client, xsrf, user) = await api.SignedInAsync();
        var body = new { columns = new[] { "dv01" }, quickFilter = $"cache-{Guid.NewGuid():N}"[..12] };

        var miss = await client.SendAsync(Query(xsrf, body), Ct);
        Assert.Equal("MISS", miss.Headers.GetValues("X-Cache").Single());
        Assert.Matches(@"^db;dur=[\d.]+, ser;dur=[\d.]+, total;dur=[\d.]+$", miss.Headers.GetValues("Server-Timing").Single());
        var etag = miss.Headers.ETag!.ToString();
        Assert.StartsWith("W/\"2026-10-06:", etag);

        var hit = await client.SendAsync(Query(xsrf, body), Ct);
        Assert.Equal("HIT", hit.Headers.GetValues("X-Cache").Single());
        Assert.Equal(await miss.Content.ReadAsStringAsync(Ct), await hit.Content.ReadAsStringAsync(Ct));

        var conditional = Query(xsrf, body);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", etag);
        var notModified = await client.SendAsync(conditional, Ct);
        Assert.Equal(HttpStatusCode.NotModified, notModified.StatusCode);

        var other = await client.SendAsync(Query(xsrf, new { columns = new[] { "cs01" }, quickFilter = body.quickFilter }), Ct);
        Assert.NotEqual(etag, other.Headers.ETag!.ToString());

        var audit = await api.WaitForAuditAsync(a => a.UserName == user.Email && a.Endpoint == "POST /api/positions/query", atLeast: 3);
        Assert.Contains(audit, a => a.Cache == "MISS" && a.Rows == 0);
        Assert.Contains(audit, a => a.Cache == "HIT");
    }

    [Fact]
    public async Task MessagePack_is_served_on_accept_and_decodes_to_the_same_shape()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var body = new { columns = new[] { "deal_name", "market_value", "spread_bp", "watchlist_flag", "next_pay_date", "vintage" }, endRow = 3 };
        var json = await ReadJson(await client.SendAsync(Query(xsrf, body), Ct));

        var res = await client.SendAsync(Query(xsrf, body, ColumnarSerializer.MsgPackContentType), Ct);
        Assert.Equal(ColumnarSerializer.MsgPackContentType, res.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(":mp\"", res.Headers.ETag!.ToString());
        var doc = MessagePack.MessagePackSerializer.Deserialize<Dictionary<string, object>>(await res.Content.ReadAsByteArrayAsync(Ct), cancellationToken: Ct);
        Assert.Equal(json.GetProperty("rowCount").GetInt32(), Convert.ToInt32(doc["rowCount"]));
        Assert.Equal(7, ((object[])doc["columns"]).Length);
        Assert.Equal(3, ((object[])((object[])doc["data"])[1]).Length);
        Assert.Equal(json.GetProperty("summary").GetProperty("market_value").GetDecimal(),
            decimal.Parse((string)((Dictionary<object, object>)doc["summary"])["market_value"], System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Every_column_kind_serializes()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var all = ColumnCatalog.PositionSnapshot.Select(c => c.Name).ToArray();
        var doc = await ReadJson(await client.SendAsync(Query(xsrf, new { columns = all, endRow = 50 }), Ct));
        Assert.Equal(all.Length, doc.GetProperty("columns").GetArrayLength());
        var res = await client.SendAsync(Query(xsrf, new { columns = all, endRow = 50 }, ColumnarSerializer.MsgPackContentType), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Fact]
    public async Task Weighted_average_over_zero_weight_rows_is_null_not_nan()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var doc = await ReadJson(await client.SendAsync(Query(xsrf, new
        {
            columns = new[] { "market_value", "spread_bp" },
            filterModel = new { market_value = new { filterType = "number", type = "equals", filter = 0 } },
        }), Ct));
        Assert.True(doc.GetProperty("rowCount").GetInt32() > 0, "the seed has zero-face positions (README §5.2)");
        Assert.Equal(JsonValueKind.Null, doc.GetProperty("summary").GetProperty("spread_bp").ValueKind);

        var empty = await ReadJson(await client.SendAsync(Query(xsrf, new
        {
            columns = new[] { "market_value", "spread_bp" },
            filterModel = new { sector = new { filterType = "set", values = Array.Empty<string>() } },
        }), Ct));
        Assert.Equal(0, empty.GetProperty("rowCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("summary").GetProperty("spread_bp").ValueKind);
        Assert.Equal(JsonValueKind.Null, empty.GetProperty("summary").GetProperty("market_value").ValueKind);
    }

    [Fact]
    public async Task The_prior_business_day_is_queryable_and_an_unknown_date_is_400()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var prior = await ReadJson(await client.SendAsync(Query(xsrf, new { asOf = "2026-10-05", columns = new[] { "dv01" }, endRow = 1 }), Ct));
        Assert.Equal("2026-10-05", prior.GetProperty("asOf").GetString());

        var res = await client.SendAsync(Query(xsrf, new { asOf = "2020-01-01" }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("Unknown as-of date", (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
    }

    [Fact]
    public async Task Query_needs_a_session_and_the_antiforgery_header()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().PostAsJsonAsync("/api/positions/query", new { }, Ct)).StatusCode);
        var (client, _, _) = await api.SignedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/positions/query", new { }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Responses_are_compressed_with_brotli()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var req = Query(xsrf, new { columns = RiskColumns });
        req.Headers.AcceptEncoding.ParseAdd("br");
        var res = await client.SendAsync(req, Ct);
        Assert.Equal("br", res.Content.Headers.ContentEncoding.Single());
    }

    [Fact]
    public async Task Export_streams_csv_with_catalog_headers_and_every_filtered_row()
    {
        var (client, xsrf, user) = await api.SignedInAsync();
        var body = new
        {
            columns = new[] { "deal_name", "market_value", "price", "spread_bp", "yield", "next_pay_date", "watchlist_flag", "vintage" },
            filterModel = new { sector = new { filterType = "set", values = new[] { "CLO" } } },
            sortModel = new[] { new { colId = "market_value", sort = "desc" } },
        };
        var res = await client.SendAsync(Query(xsrf, body, path: "/api/positions/export"), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/csv", res.Content.Headers.ContentType?.MediaType);
        Assert.Contains("positions-2026-10-06.csv", res.Content.Headers.ContentDisposition!.ToString());

        var lines = (await res.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("Position,Deal,Market value,Price,Spread,Yield,Next pay,Watchlist,Vintage".Split(',').Length, lines[0].Split(',').Length);
        Assert.StartsWith("Position,Deal,", lines[0]);
        Assert.Equal(await ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot WHERE as_of_date = DATE '2026-10-06' AND sector = 'CLO'"), lines.Length - 1);
        var first = lines[1].Split(',');
        Assert.Matches(@"^\d+\.\d{2}$", first[2]);   // money: 2 dp
        Assert.Matches(@"^\d+\.\d{3}$", first[3]);   // price: 3 dp
        Assert.Matches(@"^-?\d+$", first[4]);        // bp: whole numbers
        await api.WaitForAuditAsync(a => a.UserName == user.Email && a.Endpoint == "POST /api/positions/export" && a.Rows == lines.Length - 1);
    }

    [Fact]
    public async Task Export_formats_every_column_kind_and_leaves_nulls_empty()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var all = ColumnCatalog.PositionSnapshot.Select(c => c.Name).ToArray();
        var res = await client.SendAsync(Query(xsrf, new { columns = all }, path: "/api/positions/export"), Ct);
        var lines = (await res.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(await ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot WHERE as_of_date = DATE '2026-10-06'"), lines.Length - 1);
        Assert.Contains(lines.Skip(1), l => l.Contains(",,")); // some measures are NULL (README §5.2 edge cases)
        Assert.Contains(lines.Skip(1), l => l.Contains(",true,") || l.Contains(",false,"));
    }

    [Fact]
    public async Task A_set_filter_too_large_to_apply_is_400_not_silently_widened()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var values = Enumerable.Range(0, GridQueryNormalizer.MaxSetValues + 1).Select(i => $"v{i}").ToArray();
        var res = await client.SendAsync(Query(xsrf, new { filterModel = new { cusip = new { filterType = "set", values } } }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("Filter too large", (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
        var export = await client.SendAsync(Query(xsrf, new { filterModel = new { cusip = new { filterType = "set", values } } }, path: "/api/positions/export"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, export.StatusCode);
    }

    [Fact]
    public async Task Crafted_filter_text_cannot_share_a_cache_entry_with_a_different_query()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        // Under the old delimiter-joined key these two produced the same key and ETag.
        var crafted = await client.SendAsync(Query(xsrf, new
        {
            columns = new[] { "deal_name" },
            filterModel = new { @class = new { filterType = "text", type = "contains", filter = "x,,);deal_name:Text.Contains(y" } },
        }), Ct);
        var real = await client.SendAsync(Query(xsrf, new
        {
            columns = new[] { "deal_name" },
            filterModel = new Dictionary<string, object>
            {
                ["class"] = new { filterType = "text", type = "contains", filter = "x" },
                ["deal_name"] = new { filterType = "text", type = "contains", filter = "y" },
            },
        }), Ct);
        Assert.NotEqual(crafted.Headers.ETag!.ToString(), real.Headers.ETag!.ToString());
        Assert.Equal("MISS", real.Headers.GetValues("X-Cache").Single());
    }

    [Fact]
    public async Task Admin_cache_clear_drops_cached_blocks()
    {
        var (admin, xsrf, _) = await api.SignedInAsync(Desk.Data.Auth.Roles.Admin);
        var body = new { columns = new[] { "jtd" }, quickFilter = "clear-test" };
        await admin.SendAsync(Query(xsrf, body), Ct);
        Assert.Equal("HIT", (await admin.SendAsync(Query(xsrf, body), Ct)).Headers.GetValues("X-Cache").Single());

        Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(Query(xsrf, new { }, path: "/api/admin/cache/clear"), Ct)).StatusCode);
        Assert.Equal("MISS", (await admin.SendAsync(Query(xsrf, body), Ct)).Headers.GetValues("X-Cache").Single());

        var (viewer, viewerXsrf, _) = await api.SignedInAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.SendAsync(Query(viewerXsrf, new { }, path: "/api/admin/cache/clear"), Ct)).StatusCode);
    }

    [Fact]
    public async Task Export_of_an_unknown_date_is_400()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var res = await client.SendAsync(Query(xsrf, new { asOf = "2020-01-01" }, path: "/api/positions/export"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_requests_share_one_meta_load_and_invalidate_reloads()
    {
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(api.Time.GetUtcNow());
        var cache = new MetaCache(api.Services.GetRequiredService<MetaRepository>(), time);
        var first = cache.GetWithStatusAsync(Ct).AsTask();
        var second = cache.GetWithStatusAsync(Ct).AsTask(); // waits on the gate, then finds the fresh snapshot
        Assert.Same((await first).Snapshot, (await second).Snapshot);
        Assert.NotNull((await first).LoadMs);
        Assert.Null((await second).LoadMs);
        Assert.Same((await first).Snapshot, await cache.GetAsync(Ct));

        cache.Invalidate();
        var reloaded = await cache.GetAsync(Ct);
        Assert.NotSame((await first).Snapshot, reloaded);

        // Re-read while traffic continues, so a reseed is noticed within minutes; never past the batch.
        Assert.True(reloaded.ExpiresAt <= reloaded.BatchEndsAt);
        time.Advance(MetaCache.Revalidate);
        Assert.NotSame(reloaded, await cache.GetAsync(Ct));
    }

    [Fact]
    public async Task One_export_at_a_time_per_user()
    {
        var (client, xsrf, user) = await api.SignedInAsync();
        var gate = api.Services.GetRequiredService<Desk.Api.Limits.ExportGate>();
        Assert.Equal(Desk.Api.Limits.ExportGate.Result.Started, gate.TryBegin(user.Email!));
        try
        {
            var res = await client.SendAsync(Query(xsrf, new { columns = new[] { "deal_name" } }, path: "/api/positions/export"), Ct);
            Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
            Assert.Equal("5", res.Headers.GetValues("Retry-After").Single());
        }
        finally
        {
            gate.End(user.Email!);
        }
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(Query(xsrf, new { columns = new[] { "deal_name" } }, path: "/api/positions/export"), Ct)).StatusCode);
    }

    [Fact]
    public void Csv_escapes_commas_quotes_and_newlines()
    {
        Assert.Equal("plain", Csv.Escape("plain"));
        Assert.Equal("\"a,b\"", Csv.Escape("a,b"));
        Assert.Equal("\"say \"\"hi\"\"\"", Csv.Escape("say \"hi\""));
        Assert.Equal("\"two\nlines\"", Csv.Escape("two\nlines"));
        Assert.Equal("'=HYPERLINK(x)", Csv.Escape("=HYPERLINK(x)"));    // formula injection: neutralised
        Assert.Equal("\"'=SUM(A1,B1)\"", Csv.Escape("=SUM(A1,B1)"));   // ...and still quoted when it has a comma
        Assert.Equal("'+1", Csv.Escape("+1"));
        Assert.Equal("'-1", Csv.Escape("-1"));
        Assert.Equal("'@x", Csv.Escape("@x"));
        Assert.Equal("'\tx", Csv.Escape("\tx"));
        Assert.Equal("", Csv.Escape(""));
    }
}
