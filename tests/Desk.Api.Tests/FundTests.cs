using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Desk.Api.Positions;
using Desk.Data.Funds;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Desk.Data;
using Desk.Data.Sources;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>README §6 P2 acceptance on the seeded database (SEED=42, as-of 2026-10-06: data through 2026-09-30).</summary>
[Collection(ApiCollection.Name)]
public sealed class FundTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<int> MidMonthFundAsync()
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT fund_id FROM core.fund WHERE extract(day FROM inception_date) <> extract(day FROM (date_trunc('month', inception_date) + interval '1 month - 1 day')) LIMIT 1", conn);
        return (int)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    [Theory]
    [InlineData("QTD", 3)]
    [InlineData("YTD", 9)]
    [InlineData("1Y", 12)]
    public async Task Preset_ranges_have_the_right_month_counts(string range, int months)
    {
        var (client, _, _) = await api.SignedInAsync();
        var p = await client.GetFromJsonAsync<FundPerformance>($"/api/funds/1/performance?range={range}", Ct);
        Assert.Equal(months, p!.Months.Length);
        Assert.Equal("2026-09-30", p.Months[^1]);
        Assert.All(p.Rows, r => Assert.Equal(months, r.Values.Length));
        Assert.Equal(["Balance", "IRR"], p.Rows.Select(r => r.Label));
    }

    [Fact]
    public async Task Itd_of_a_mid_month_inception_starts_at_its_first_month_end()
    {
        var fundId = await MidMonthFundAsync();
        var (client, _, _) = await api.SignedInAsync();
        var p = await client.GetFromJsonAsync<FundPerformance>($"/api/funds/{fundId}/performance?range=ITD", Ct);
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT (date_trunc('month', inception_date) + interval '1 month - 1 day')::date FROM core.fund WHERE fund_id = @id", conn);
        cmd.Parameters.AddWithValue("id", fundId);
        var firstMonthEnd = (DateOnly)(await cmd.ExecuteScalarAsync(Ct))!;
        Assert.Equal(firstMonthEnd.ToString("yyyy-MM-dd"), p!.Months[0]);
        Assert.Equal(p.Months.OrderBy(m => m, StringComparer.Ordinal), p.Months);
    }

    [Fact]
    public async Task Custom_ranges_and_ranges_without_data()
    {
        var (client, _, _) = await api.SignedInAsync();
        var p = await client.GetFromJsonAsync<FundPerformance>("/api/funds/1/performance?range=CUSTOM&from=2025-01-15&to=2025-06-01", Ct);
        Assert.Equal(["2025-01-31", "2025-02-28", "2025-03-31", "2025-04-30", "2025-05-31", "2025-06-30"], p!.Months);

        var none = await client.GetFromJsonAsync<FundPerformance>("/api/funds/1/performance?range=CUSTOM&from=2001-01-01&to=2001-12-31", Ct);
        Assert.Empty(none!.Months);
        Assert.All(none.Rows, r => Assert.Empty(r.Values));
        Assert.Null(none.From);
    }

    [Fact]
    public async Task Custom_months_compare_and_cache_as_month_ends()
    {
        var (client, _, _) = await api.SignedInAsync();
        // Both days are June 2025: one month, not a reversed range.
        var june = await client.GetAsync("/api/funds/1/performance?range=CUSTOM&from=2025-06-15&to=2025-06-01", Ct);
        Assert.Equal(HttpStatusCode.OK, june.StatusCode);
        Assert.Equal(["2025-06-30"], (await june.Content.ReadFromJsonAsync<FundPerformance>(Ct))!.Months);
        // Any day of the same months is the same entry and ETag.
        var other = await client.GetAsync("/api/funds/1/performance?range=CUSTOM&from=2025-06-01&to=2025-06-30", Ct);
        Assert.Equal(june.Headers.ETag, other.Headers.ETag);
        Assert.Equal("HIT", other.Headers.GetValues("X-Cache").Single());
    }

    [Theory]
    [InlineData("range=MTD")]
    [InlineData("range=CUSTOM")]
    [InlineData("range=CUSTOM&from=2025-01-01")]
    [InlineData("range=CUSTOM&to=2025-01-01")]
    [InlineData("range=CUSTOM&from=2025-06-01&to=2025-01-01")]
    public async Task Bad_ranges_are_400(string query)
    {
        var (client, _, _) = await api.SignedInAsync();
        var res = await client.GetAsync($"/api/funds/1/performance?{query}", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_funds_are_404_and_the_default_range_is_ytd()
    {
        var (client, _, _) = await api.SignedInAsync();
        var res = await client.GetAsync("/api/funds/999/performance", Ct);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal("No such fund", (await res.Content.ReadFromJsonAsync<ProblemDetails>(Ct))!.Title);
        Assert.Equal("YTD", (await client.GetFromJsonAsync<FundPerformance>("/api/funds/2/performance", Ct))!.Range);
    }

    /// <summary>Grants a fixed set of portfolios to everyone (README §8 entitlements, #123 stub-provider rule).</summary>
    private sealed class StubEntitlements(params int[] portfolioIds) : IPortfolioEntitlements
    {
        public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta) => portfolioIds;
    }

    private async Task<HttpClient> ClientWithAsync(StubEntitlements entitlements)
    {
        var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPortfolioEntitlements>(entitlements)));
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        return client;
    }

    [Fact]
    public async Task A_fund_is_visible_only_with_every_one_of_its_portfolios()
    {
        var (full, _, _) = await api.SignedInAsync();
        var unknown = await full.GetAsync("/api/funds/999/performance", Ct);
        var visible = await full.GetAsync("/api/funds/1/performance?range=ITD", Ct);
        var etag = visible.Headers.ETag!.ToString();

        // Fund 2 in full (2, 6, 10) plus one of fund 1's three portfolios.
        var partial = await ClientWithAsync(new StubEntitlements(2, 6, 10, 1));
        Assert.Equal(HttpStatusCode.OK, (await partial.GetAsync("/api/funds/2/performance", Ct)).StatusCode);
        var hidden = await partial.GetAsync("/api/funds/1/performance?range=ITD", Ct);
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        Assert.Null(hidden.Headers.ETag);
        // Indistinguishable from a fund that doesn't exist.
        var (u, h) = (await unknown.Content.ReadFromJsonAsync<ProblemDetails>(Ct), await hidden.Content.ReadFromJsonAsync<ProblemDetails>(Ct));
        Assert.Equal((u!.Status, u.Title, u.Type, u.Detail!.Replace("999", "1")), (h!.Status, h.Title, h.Type, h.Detail));

        // A cached response and a valid ETag never leak past the check: 404, not 304 or the cached body.
        using var revalidate = new HttpRequestMessage(HttpMethod.Get, "/api/funds/1/performance?range=ITD");
        revalidate.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotFound, (await partial.SendAsync(revalidate, Ct)).StatusCode);

        var none = await ClientWithAsync(new StubEntitlements());
        foreach (var fund in new[] { 1, 2, 3, 4 })
            Assert.Equal(HttpStatusCode.NotFound, (await none.GetAsync($"/api/funds/{fund}/performance", Ct)).StatusCode);
    }

    /// <summary>Counts connections opened on the <c>core</c> source (the fund reads); audit writes use <c>app</c>.</summary>
    private sealed class CoreOpens(DataSourceRegistry inner) : IDataSourceRegistry
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public NpgsqlDataSource Get(string source) => inner.Get(source);
        public ValueTask<NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
        {
            if (source == ConnectionStrings.Core) Interlocked.Increment(ref _count);
            return inner.OpenAsync(source, ct);
        }
    }

    [Fact]
    public async Task A_fund_without_performance_history_is_404_without_an_etag_and_without_a_second_query()
    {
        // A throwaway fund + portfolio far outside any seeded id, created and removed by this test alone.
        const int Id = 900_001;
        async Task Exec(string sql)
        {
            await using var conn = new NpgsqlConnection(api.ConnectionString);
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("id", Id);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        const string Cleanup = "DELETE FROM core.portfolio WHERE portfolio_id = @id; DELETE FROM core.fund WHERE fund_id = @id;";
        try
        {
            await Exec(Cleanup); // idempotent: a previous run that died mid-test left nothing behind
            await Exec("""
                INSERT INTO core.fund (fund_id, name, inception_date, strategy) VALUES (@id, 'Test Fund (no history)', DATE '2026-09-01', 'test');
                INSERT INTO core.portfolio (portfolio_id, fund_id, name, manager, benchmark) VALUES (@id, @id, 'Test Portfolio', 'test', 'none');
                """);
            // A private host: its own (empty) caches load the new portfolio, and the shared fixture's caches are untouched.
            CoreOpens? opens = null;
            await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
            {
                s.AddSingleton<DataSourceRegistry>();
                s.AddSingleton<IDataSourceRegistry>(sp => opens = new CoreOpens(sp.GetRequiredService<DataSourceRegistry>()));
            }));
            var client = PostgresApiFactory.NewClient(host);
            await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);

            var first = await client.GetAsync($"/api/funds/{Id}/performance?range=QTD", Ct);
            Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
            Assert.Null(first.Headers.ETag);

            // "No data" is cached too (cache-first): another range for the same fund reads nothing.
            var before = opens!.Count;
            var second = await client.GetAsync($"/api/funds/{Id}/performance?range=YTD", Ct);
            Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
            Assert.Null(second.Headers.ETag);
            Assert.Equal(before, opens.Count);
        }
        finally
        {
            await Exec(Cleanup);
        }
    }

    [Fact]
    public async Task Repeat_is_a_cache_hit_and_if_none_match_is_304_and_the_payload_is_small()
    {
        var (client, _, _) = await api.SignedInAsync();
        // Fund 1 ITD is the largest P2 payload (69 months).
        var miss = await client.GetAsync("/api/funds/1/performance?range=ITD", Ct);
        Assert.True((await miss.Content.ReadAsByteArrayAsync(Ct)).Length < 5 * 1024, "README §6 P2: payload < 5 KB");
        var hit = await client.GetAsync("/api/funds/1/performance?range=ITD", Ct);
        Assert.Equal("HIT", hit.Headers.GetValues("X-Cache").Single());

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/funds/1/performance?range=ITD");
        req.Headers.TryAddWithoutValidation("If-None-Match", miss.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(req, Ct)).StatusCode);
    }
}
