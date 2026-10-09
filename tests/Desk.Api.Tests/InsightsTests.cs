using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Desk.Api.Insights;
using Desk.Api.Positions;
using Desk.Data.Insights;
using Desk.Data.Sources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>README §6 P3 on the seeded database (SEED=42, scale 0.1, as-of 2026-10-06).</summary>
[Collection(ApiCollection.Name)]
public sealed class InsightsTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const int P1 = 1;

    private sealed class StubEntitlements(params int[] ids) : IPortfolioEntitlements
    {
        public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta) => ids;
    }

    private async Task<(WebApplicationFactory<Program> Host, HttpClient Client)> HostWithAsync(params int[] portfolios)
    {
        var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPortfolioEntitlements>(new StubEntitlements(portfolios))));
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        return (host, client);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private static Dictionary<string, JsonElement> Grids(JsonElement body) =>
        body.GetProperty("grids").EnumerateArray().ToDictionary(g => g.GetProperty("id").GetString()!);

    private static string[] Strings(JsonElement array) => [.. array.EnumerateArray().Select(e => e.GetString()!)];

    private static decimal? Dec(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDecimal();

    private async Task<List<(string Key, decimal Value)>> SqlAsync(string sql, int[]? portfolios = null)
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("asOf", PostgresApiFactory.AsOf);
        cmd.Parameters.AddWithValue("p", portfolios ?? Enumerable.Range(1, 12).ToArray());
        var rows = new List<(string, decimal)>();
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct)) rows.Add((r.GetString(0), r.GetDecimal(1)));
        return rows;
    }

    [Fact]
    public async Task Every_source_returns_its_grids_lined_up_for_the_whole_book()
    {
        var (client, _, _) = await api.SignedInAsync();
        foreach (var source in InsightCatalog.Sources)
        {
            var body = await JsonAsync(await client.GetAsync($"/api/insights/{source}", Ct));
            Assert.Equal(source, body.GetProperty("source").GetString());
            Assert.Equal("2026-10-06", body.GetProperty("asOf").GetString());
            var grids = body.GetProperty("grids").EnumerateArray().ToArray();
            Assert.Equal(InsightCatalog.For(source).Select(s => s.Id), grids.Select(g => g.GetProperty("id").GetString()));
            foreach (var g in grids)
            {
                var columns = Strings(g.GetProperty("columns"));
                var rows = g.GetProperty("rows").EnumerateArray().ToArray();
                Assert.True(rows.Length > 0, $"{source}/{g.GetProperty("id")} is empty on the full book");
                Assert.All(rows, r => Assert.Equal(columns.Length, r.GetArrayLength()));
                Assert.All(rows, r => Assert.Equal(JsonValueKind.String, r[0].ValueKind)); // the row label
                Assert.All(columns, c => Assert.True(g.GetProperty("format").TryGetProperty(c, out _), $"{c} has no format"));
            }
        }
    }

    [Fact]
    public async Task Source_names_are_case_insensitive_and_unknown_ones_are_404()
    {
        var (client, _, _) = await api.SignedInAsync();
        Assert.Equal("market", (await JsonAsync(await client.GetAsync("/api/insights/Market", Ct))).GetProperty("source").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/insights/app", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/insights/core?grid=curve_moves", Ct)).StatusCode);
    }

    [Fact]
    public async Task Naive_mode_reads_one_grid()
    {
        var (client, _, _) = await api.SignedInAsync();
        var body = await JsonAsync(await client.GetAsync("/api/insights/market?grid=curve_moves", Ct));
        var grid = Assert.Single(body.GetProperty("grids").EnumerateArray());
        Assert.Equal("curve_moves", grid.GetProperty("id").GetString());
        Assert.Equal(["3M", "2Y", "5Y", "10Y", "30Y"], grid.GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()));
    }

    [Theory]
    [InlineData("/api/insights/core?portfolioIds=1,x")]
    [InlineData("/api/insights/core?portfolioIds=-1")]
    [InlineData("/api/insights/core?asOf=2001-01-01")]
    public async Task Bad_parameters_are_400(string url)
    {
        var (client, _, _) = await api.SignedInAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(url, Ct)).StatusCode);
    }

    [Fact]
    public async Task The_prior_business_day_is_a_valid_as_of()
    {
        var (client, _, _) = await api.SignedInAsync();
        var body = await JsonAsync(await client.GetAsync("/api/insights/core?asOf=2026-10-05", Ct));
        Assert.Equal("2026-10-05", body.GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task Repeat_reads_are_cache_hits_and_revalidate_to_304()
    {
        var (client, _, _) = await api.SignedInAsync();
        var miss = await client.GetAsync("/api/insights/reference?portfolioIds=2,3", Ct);
        var etag = miss.Headers.ETag!.ToString();
        Assert.Contains("total;dur=", miss.Headers.GetValues("Server-Timing").Single());
        var hit = await client.GetAsync("/api/insights/reference?portfolioIds=3,2", Ct); // same set, any order
        Assert.Equal("HIT", hit.Headers.GetValues("X-Cache").Single());
        Assert.Equal(etag, hit.Headers.ETag!.ToString());
        Assert.Equal(await miss.Content.ReadAsByteArrayAsync(Ct), await hit.Content.ReadAsByteArrayAsync(Ct));

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/insights/reference?portfolioIds=2,3");
        req.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(req, Ct)).StatusCode);

        var other = await client.GetAsync("/api/insights/reference?portfolioIds=2", Ct);
        Assert.NotEqual(etag, other.Headers.ETag!.ToString());
        var grid = await client.GetAsync("/api/insights/reference?portfolioIds=2,3&grid=trustee_exposure", Ct);
        Assert.NotEqual(etag, grid.Headers.ETag!.ToString());
    }

    // ---------- ADR-0021: a stub provider decides what the SQL sees ----------

    [Fact]
    public async Task A_single_portfolio_sees_only_its_own_book()
    {
        var (host, client) = await HostWithAsync(P1);
        await using var _ = host;
        var core = Grids(await JsonAsync(await client.GetAsync("/api/insights/core", Ct)));

        // MV by sector × rating sums to the portfolio's own MV per sector.
        var expected = await SqlAsync(
            "SELECT sector, sum(market_value) FROM core.position_snapshot WHERE as_of_date = @asOf AND portfolio_id = ANY(@p) GROUP BY sector", [P1]);
        var actual = core["mv_sector_rating"].GetProperty("rows").EnumerateArray()
            .Select(r => (Key: r[0].GetString()!, Value: r.EnumerateArray().Skip(1).Sum(c => Dec(c) ?? 0)))
            .ToList();
        Assert.Equal(expected.OrderBy(x => x.Key), actual.OrderBy(x => x.Key));

        // P&L by portfolio has exactly this portfolio.
        var name = (await SqlAsync("SELECT name, 0::numeric FROM core.portfolio WHERE portfolio_id = ANY(@p)", [P1])).Single().Key;
        Assert.Equal([name], core["pnl_attribution_portfolio"].GetProperty("rows").EnumerateArray().Select(r => r[0].GetString()));

        // Requesting a portfolio outside the entitlement doesn't widen the scope: it's dropped, leaving nothing.
        var outside = Grids(await JsonAsync(await client.GetAsync("/api/insights/core?portfolioIds=2", Ct)));
        Assert.All(outside.Values, g => Assert.Equal(0, g.GetProperty("rows").GetArrayLength()));

        // Reference exposure shares are of this portfolio's book only.
        var reference = Grids(await JsonAsync(await client.GetAsync("/api/insights/reference", Ct)));
        var mv = expected.Sum(x => x.Value);
        var trustees = await SqlAsync(
            "SELECT t.name, sum(p.market_value) FROM core.position_snapshot p JOIN core.deal d USING (deal_id) JOIN reference.trustee t USING (trustee_id) "
            + "WHERE p.as_of_date = @asOf AND p.portfolio_id = ANY(@p) GROUP BY t.name ORDER BY 2 DESC LIMIT 5", [P1]);
        var rows = reference["trustee_exposure"].GetProperty("rows").EnumerateArray().ToArray();
        Assert.Equal(trustees.Select(t => t.Key), rows.Select(r => r[0].GetString()));
        Assert.Equal(trustees[0].Value / mv, Dec(rows[0][2])!.Value, 10);
    }

    [Fact]
    public async Task An_empty_entitlement_returns_zero_rows_from_every_source()
    {
        var (host, client) = await HostWithAsync();
        await using var _ = host;
        foreach (var source in InsightCatalog.Sources)
        {
            var grids = Grids(await JsonAsync(await client.GetAsync($"/api/insights/{source}", Ct)));
            Assert.Equal(InsightCatalog.For(source).Count, grids.Count);
            Assert.All(grids.Values, g => Assert.Equal(0, g.GetProperty("rows").GetArrayLength()));
            Assert.All(grids.Values, g => Assert.True(g.GetProperty("columns").GetArrayLength() > 1));
        }
    }

    // ---------- README §6 P3 grain: aggregate the child before joining the parent ----------

    [Fact]
    public async Task Deal_original_balance_by_sector_is_counted_once_per_deal()
    {
        var (client, _, _) = await api.SignedInAsync();
        var core = Grids(await JsonAsync(await client.GetAsync("/api/insights/core", Ct)));
        var shown = core["watchlist_sector"].GetProperty("rows").EnumerateArray()
            .Select(r => (Key: r[0].GetString()!, Value: Dec(r[2])!.Value)).OrderBy(x => x.Key).ToList();

        var perDeal = await SqlAsync(
            "SELECT sector, sum(original_balance) FROM core.deal WHERE deal_id IN "
            + "(SELECT deal_id FROM core.position_snapshot WHERE as_of_date = @asOf AND portfolio_id = ANY(@p)) GROUP BY sector");
        Assert.Equal(perDeal.OrderBy(x => x.Key), shown);

        // The naive join sums a deal's balance once per position held in it: strictly more wherever a deal has
        // several positions, which the seeded book has in every sector.
        var naive = await SqlAsync(
            "SELECT d.sector, sum(d.original_balance) FROM core.position_snapshot p JOIN core.deal d USING (deal_id) "
            + "WHERE p.as_of_date = @asOf AND p.portfolio_id = ANY(@p) GROUP BY d.sector");
        Assert.All(shown, s => Assert.True(naive.Single(n => n.Key == s.Key).Value > s.Value, $"{s.Key}: the naive join should double count"));
    }

    [Fact]
    public async Task Oc_cushion_held_mv_matches_the_positions_in_those_deals()
    {
        var (client, _, _) = await api.SignedInAsync();
        var grid = Grids(await JsonAsync(await client.GetAsync("/api/insights/surveillance", Ct)))["oc_cushion_buckets"];
        var total = grid.GetProperty("rows").EnumerateArray().Sum(r => Dec(r[2]) ?? 0);
        var expected = (await SqlAsync(
            "SELECT 'all', sum(market_value) FROM core.position_snapshot WHERE as_of_date = @asOf AND portfolio_id = ANY(@p) "
            + "AND deal_id IN (SELECT deal_id FROM surveillance.deal_remit WHERE period <= @asOf AND oc_cushion IS NOT NULL)")).Single().Value;
        Assert.Equal(expected, total);
    }

    // ---------- the dev-only delay (README §6 P3 progressive-render acceptance) ----------

    [Fact]
    public async Task The_delay_header_is_honoured_only_when_fault_injection_is_on()
    {
        await using var host = api.WithSettings(("DEV_FAULT_INJECTION", "true"));
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/insights/market");
        req.Headers.Add(InsightsFaults.DelayHeader, "400");
        var started = Stopwatch.GetTimestamp();
        Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(req, Ct)).StatusCode);
        Assert.True(Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMilliseconds(400));

        // The shared fixture runs as Production without the flag: the header does nothing there.
        Assert.False(api.Services.GetRequiredService<InsightsFaults>().Enabled);
    }

    [Theory]
    [InlineData(true, "250", 250)]
    [InlineData(true, "999999", InsightsFaults.MaxDelayMs)]
    [InlineData(true, "-5", 0)]
    [InlineData(true, "soon", 0)]
    [InlineData(true, null, 0)]
    [InlineData(false, "250", 0)]
    public void Delay_is_clamped_and_off_unless_enabled(bool enabled, string? header, int expectedMs)
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        if (header is not null) http.Request.Headers[InsightsFaults.DelayHeader] = header;
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), new InsightsFaults(enabled).DelayFor(http.Request));
    }

    [Theory]
    [InlineData("Production", null, false)]
    [InlineData("Production", "TRUE", false)]
    [InlineData("Production", "true", true)]
    [InlineData("Development", null, true)]
    public void Fault_injection_is_development_or_an_explicit_flag(string environment, string? flag, bool enabled)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["DEV_FAULT_INJECTION"] = flag }).Build();
        Assert.Equal(enabled, InsightsFaults.From(config, new Env(environment)).Enabled);
    }

    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Desk.Api";
        public string ContentRootPath { get; set; } = "/";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Theory]
    [InlineData(null, new int[0])]
    [InlineData(" ", new int[0])]
    [InlineData("3,7", new[] { 3, 7 })]
    [InlineData(" 3 , 7 ,", new[] { 3, 7 })]
    public void Portfolio_ids_parse(string? text, int[] expected)
    {
        Assert.True(InsightsEndpoints.TryParseIds(text, out var ids));
        Assert.Equal(expected, ids);
    }

    [Theory]
    [InlineData("1,a")]
    [InlineData("+1")]
    [InlineData("99999999999")]
    public void Bad_portfolio_ids_are_refused(string text) => Assert.False(InsightsEndpoints.TryParseIds(text, out _));

    [Fact]
    public void More_than_a_hundred_ids_are_refused() =>
        Assert.False(InsightsEndpoints.TryParseIds(string.Join(',', Enumerable.Range(1, 101)), out _));
}

/// <summary>
/// README §6 P3 / §11: a DbContext or connection shared across concurrent tasks throws; one per task passes. The
/// shared versions are what the insights fan-out must never do.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class InsightsConcurrencyTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly int[] All = [.. Enumerable.Range(1, 12)];

    [Fact]
    public async Task A_shared_DbContext_across_Task_WhenAll_throws()
    {
        await using var shared = api.NewContext();
        var e = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => shared.ColumnCatalog.AsNoTracking().Select(c => c.Name).ToListAsync(Ct))));
        Assert.Contains("second operation", e.Message);
    }

    [Fact]
    public async Task One_DbContext_per_task_from_the_factory_passes()
    {
        var factory = api.Services.GetRequiredService<IDbContextFactory<Desk.Data.App.AppDbContext>>();
        var counts = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            await using var ctx = await factory.CreateDbContextAsync(Ct);
            return await ctx.ColumnCatalog.AsNoTracking().CountAsync(Ct);
        }));
        Assert.All(counts, c => Assert.Equal(counts[0], c));
        Assert.True(counts[0] > 0);
    }

    [Fact]
    public async Task A_shared_connection_across_the_grids_throws()
    {
        await using var shared = new NpgsqlConnection(api.ConnectionString);
        await shared.OpenAsync(Ct);
        await Assert.ThrowsAsync<NpgsqlOperationInProgressException>(() => Task.WhenAll(
            InsightCatalog.For(InsightCatalog.Core).Select(spec => InsightsRepository.RunAsync(shared, spec, PostgresApiFactory.AsOf, All, Ct))));
    }

    [Fact]
    public async Task One_connection_per_grid_passes_for_all_twenty()
    {
        var repo = new InsightsRepository(api.Services.GetRequiredService<IDataSourceRegistry>(), new InsightsOptions(4, 10));
        using var _ = repo;
        var grids = await repo.ReadAsync(InsightCatalog.All, PostgresApiFactory.AsOf, All, Ct);
        Assert.Equal(InsightCatalog.All.Select(s => s.Id), grids.Select(g => g.Id));
        Assert.All(grids, g => Assert.NotEmpty(g.Rows));
    }

    [Fact]
    public async Task Fan_out_never_holds_more_connections_than_its_caps()
    {
        var registry = new PeakRegistry(api.Services.GetRequiredService<IDataSourceRegistry>());
        using var repo = new InsightsRepository(registry, new InsightsOptions(PerRequest: 2, Global: 3));
        // Two requests at once: each may hold 2, together never more than the global 3.
        await Task.WhenAll(
            repo.ReadAsync(InsightCatalog.For(InsightCatalog.Core), PostgresApiFactory.AsOf, All, Ct),
            repo.ReadAsync(InsightCatalog.For(InsightCatalog.Surveillance), PostgresApiFactory.AsOf, All, Ct));
        Assert.InRange(registry.Peak, 1, 3);
        Assert.Equal(11, registry.Opens);
    }

    /// <summary>Counts connections open at once (through the real registry).</summary>
    private sealed class PeakRegistry(IDataSourceRegistry inner) : IDataSourceRegistry
    {
        private int _open;
        public int Peak;
        public int Opens;

        public NpgsqlDataSource Get(string source) => inner.Get(source);

        public async ValueTask<NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
        {
            var conn = await inner.OpenAsync(source, ct);
            Interlocked.Increment(ref Opens);
            var now = Interlocked.Increment(ref _open);
            InterlockedMax(ref Peak, now);
            conn.StateChange += (_, e) => { if (e.CurrentState == System.Data.ConnectionState.Closed) Interlocked.Decrement(ref _open); };
            return conn;
        }

        private static void InterlockedMax(ref int target, int value)
        {
            int current;
            while ((current = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, current) != current) { }
        }
    }
}
