using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.Hardening;
using Desk.Api.Lab;
using Desk.Api.Positions;
using Desk.Data.Auth;
using Desk.Data.Catalog;
using Desk.Data.Grid;

namespace Desk.Api.Tests;

/// <summary>
/// README §6 P5 Performance Lab (#64): the server side of its toggles. Row-object JSON on request, MessagePack
/// compressed like JSON, the cache switched off for admins only, and the response encoding forced only where the
/// dev knobs are on (Development or <c>DEV_FAULT_INJECTION=true</c>).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LabTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly string[] RiskColumns = [.. BuiltInPresets.Risk];

    private static HttpRequestMessage Query(object body, string? accept = null, params (string Name, string Value)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/positions/query") { Content = JsonContent.Create(body) };
        if (accept is not null) req.Headers.Add("Accept", accept);
        foreach (var (name, value) in headers) req.Headers.Add(name, value);
        return req;
    }

    private static object Unique(params string[] columns) => new { columns, quickFilter = $"lab-{Guid.NewGuid():N}"[..12] };

    private static string XCache(HttpResponseMessage res) => res.Headers.GetValues("X-Cache").Single();

    [Fact]
    public async Task Row_json_is_served_on_accept_with_the_same_values_as_columnar()
    {
        var (client, _, _) = await api.SignedInAsync();
        var body = new { columns = new[] { "deal_name", "market_value", "spread_bp", "watchlist_flag", "next_pay_date", "vintage" }, endRow = 5 };
        using var columnar = JsonDocument.Parse(await (await client.SendAsync(Query(body), Ct)).Content.ReadAsStringAsync(Ct));

        var res = await client.SendAsync(Query(body, ColumnarSerializer.RowJsonContentType), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(ColumnarSerializer.RowJsonContentType, res.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(":rj\"", res.Headers.ETag!.ToString());
        using var rows = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));

        var names = columnar.RootElement.GetProperty("columns").EnumerateArray().Select(c => c.GetString()!).ToArray();
        var data = columnar.RootElement.GetProperty("data");
        var objects = rows.RootElement.GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(5, objects.Length);
        for (var r = 0; r < objects.Length; r++)
        {
            Assert.Equal(names, objects[r].EnumerateObject().Select(p => p.Name));
            for (var c = 0; c < names.Length; c++)
                Assert.Equal(data[c][r].GetRawText(), objects[r].GetProperty(names[c]).GetRawText());
        }
        Assert.Equal(columnar.RootElement.GetProperty("rowCount").GetInt32(), rows.RootElement.GetProperty("rowCount").GetInt32());
        Assert.Equal(columnar.RootElement.GetProperty("summary").GetRawText(), rows.RootElement.GetProperty("summary").GetRawText());
        Assert.Equal("2026-10-06", rows.RootElement.GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task Row_json_writes_every_column_kind()
    {
        var (client, _, _) = await api.SignedInAsync();
        var all = ColumnCatalog.PositionSnapshot.Select(c => c.Name).ToArray();
        var res = await client.SendAsync(Query(new { columns = all, endRow = 50 }, ColumnarSerializer.RowJsonContentType), Ct);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        var rows = doc.RootElement.GetProperty("rows");
        Assert.Equal(50, rows.GetArrayLength());
        Assert.Equal(all.Length, rows[0].EnumerateObject().Count());
    }

    [Theory]
    [InlineData(ColumnarSerializer.MsgPackContentType)]
    [InlineData(ColumnarSerializer.RowJsonContentType)]
    public async Task Every_payload_format_is_compressed(string accept)
    {
        var (client, _, _) = await api.SignedInAsync();
        var req = Query(new { columns = RiskColumns }, accept);
        req.Headers.AcceptEncoding.ParseAdd("br");
        var res = await client.SendAsync(req, Ct);
        Assert.Equal(accept, res.Content.Headers.ContentType?.MediaType);
        Assert.Equal("br", res.Content.Headers.ContentEncoding.Single());
    }

    [Fact]
    public async Task An_admin_can_switch_the_cache_off_and_nothing_is_read_or_stored()
    {
        var (client, _, user) = await api.SignedInAsync(Roles.Admin);
        var body = Unique("dv01");
        var off = (LabSwitches.CacheHeader, "off");

        var first = await client.SendAsync(Query(body, null, off), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal("BYPASS", XCache(first));
        Assert.Matches(@"^db;dur=[\d.]+, ser;dur=[\d.]+, total;dur=[\d.]+$", first.Headers.GetValues("Server-Timing").Single());

        // Still computed, never a 304: the run measures the database and the serializer.
        var conditional = Query(body, null, off);
        conditional.Headers.TryAddWithoutValidation("If-None-Match", first.Headers.ETag!.ToString());
        var again = await client.SendAsync(conditional, Ct);
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal("BYPASS", XCache(again));

        // A bypassed response was never stored, so the next normal request is a miss, then a hit.
        Assert.Equal("MISS", XCache(await client.SendAsync(Query(body), Ct)));
        Assert.Equal("HIT", XCache(await client.SendAsync(Query(body), Ct)));

        var audit = await api.WaitForAuditAsync(a => a.UserName == user.Email && a.Endpoint == "POST /api/positions/query", atLeast: 4);
        Assert.Equal(2, audit.Count(a => a.Cache == "BYPASS"));
    }

    [Fact]
    public async Task A_viewer_cannot_switch_the_cache_off()
    {
        var (client, _, _) = await api.SignedInAsync();
        var body = Unique("dv01");
        Assert.Equal("MISS", XCache(await client.SendAsync(Query(body, null, (LabSwitches.CacheHeader, "off")), Ct)));
        Assert.Equal("HIT", XCache(await client.SendAsync(Query(body, null, (LabSwitches.CacheHeader, "off")), Ct)));
    }

    [Fact]
    public async Task Only_the_exact_value_off_switches_the_cache_off()
    {
        var (client, _, _) = await api.SignedInAsync(Roles.Admin);
        var body = Unique("dv01");
        Assert.Equal("MISS", XCache(await client.SendAsync(Query(body, null, (LabSwitches.CacheHeader, "no")), Ct)));
        Assert.Equal("HIT", XCache(await client.SendAsync(Query(body, null, (LabSwitches.CacheHeader, "OFF ")), Ct)));
    }

    [Fact]
    public async Task An_admin_can_switch_the_insights_cache_off()
    {
        var (client, _, _) = await api.SignedInAsync(Roles.Admin);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/insights/market?grid=curve_moves");
        req.Headers.Add(LabSwitches.CacheHeader, "off");
        var res = await client.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("BYPASS", XCache(res));
    }

    [Theory]
    [InlineData("/api/funds/1/performance?range=ITD")]
    [InlineData("/api/deals")]
    public async Task An_admin_can_switch_the_fund_and_deal_caches_off(string url)
    {
        var (client, _, _) = await api.SignedInAsync(Roles.Admin);
        // Warm the shared cache first: a bypassed run still reads the database rather than the stored bytes.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url, Ct)).StatusCode);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Add(LabSwitches.CacheHeader, "off");
        var res = await client.SendAsync(req, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("BYPASS", XCache(res));
        Assert.Equal("HIT", XCache(await client.GetAsync(url, Ct)));
    }

    [Theory]
    [InlineData("identity", null)]
    [InlineData("gzip", "gzip")]
    [InlineData("br", "br")]
    public async Task With_the_dev_knobs_on_the_encoding_can_be_forced(string asked, string? expected)
    {
        await using var host = api.WithSettings(("DEV_FAULT_INJECTION", "true"));
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        var req = Query(new { columns = RiskColumns }, null, (LabSwitches.EncodingHeader, asked));
        req.Headers.AcceptEncoding.ParseAdd(asked == "br" ? "gzip" : "br"); // the browser's own header is overridden
        var res = await client.SendAsync(req, Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(expected, res.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Equal(asked, res.Headers.GetValues(LabSwitches.EncodingHeader).Single());
    }

    [Fact]
    public async Task An_unknown_encoding_is_ignored()
    {
        await using var host = api.WithSettings(("DEV_FAULT_INJECTION", "true"));
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        var req = Query(new { columns = RiskColumns }, null, (LabSwitches.EncodingHeader, "deflate"));
        req.Headers.AcceptEncoding.ParseAdd("br");
        var res = await client.SendAsync(req, Ct);
        Assert.Equal("br", res.Content.Headers.ContentEncoding.Single());
        Assert.False(res.Headers.Contains(LabSwitches.EncodingHeader));
    }

    [Fact]
    public async Task Production_ignores_the_encoding_override()
    {
        var (client, _, _) = await api.SignedInAsync();
        var req = Query(new { columns = RiskColumns }, null, (LabSwitches.EncodingHeader, "identity"));
        req.Headers.AcceptEncoding.ParseAdd("br");
        var res = await client.SendAsync(req, Ct);
        Assert.Equal("br", res.Content.Headers.ContentEncoding.Single());
        Assert.False(res.Headers.Contains(LabSwitches.EncodingHeader));
    }

    [Fact]
    public async Task A_cross_origin_lab_may_send_both_switches_and_read_the_applied_encoding()
    {
        const string pages = "https://pages.example.test";
        await using var host = api.WithSettings((CorsSetup.ConfigKey, pages));
        using var req = new HttpRequestMessage(HttpMethod.Options, "/api/positions/query");
        req.Headers.Add("Origin", pages);
        req.Headers.Add("Access-Control-Request-Method", "POST");
        req.Headers.Add("Access-Control-Request-Headers", $"authorization,content-type,{LabSwitches.CacheHeader},{LabSwitches.EncodingHeader}".ToLowerInvariant());
        var res = await PostgresApiFactory.NewClient(host).SendAsync(req, Ct);

        var allowed = string.Join(",", res.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains(LabSwitches.CacheHeader, allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LabSwitches.EncodingHeader, allowed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(LabSwitches.EncodingHeader, CorsSetup.ExposedHeaders);
    }
}
