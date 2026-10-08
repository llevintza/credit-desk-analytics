using System.Net;
using System.Net.Http.Json;
using Desk.Data.Funds;
using Microsoft.AspNetCore.Mvc;
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
        await using var cmd = new NpgsqlCommand($"SELECT (date_trunc('month', inception_date) + interval '1 month - 1 day')::date FROM core.fund WHERE fund_id = {fundId}", conn);
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

    [Fact]
    public async Task A_fund_without_performance_history_is_404_without_an_etag_and_without_a_second_query()
    {
        // A throwaway fund + portfolio of its own (never touches the seeded rows other tests read).
        async Task Exec(string sql)
        {
            await using var conn = new NpgsqlConnection(api.ConnectionString);
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await Exec("INSERT INTO core.fund VALUES (99, 'Test Fund (no history)', DATE '2026-09-01', 'test'); INSERT INTO core.portfolio VALUES (99, 99, 'Test Portfolio', 'test', 'none');");
        var (admin, xsrf, _) = await api.SignedInAsync(Desk.Data.Auth.Roles.Admin);
        async Task ClearCache()
        {
            using var clear = new HttpRequestMessage(HttpMethod.Post, "/api/admin/cache/clear");
            clear.Headers.Add("X-XSRF-TOKEN", xsrf);
            Assert.Equal(HttpStatusCode.NoContent, (await admin.SendAsync(clear, Ct)).StatusCode);
        }
        try
        {
            await ClearCache(); // the portfolio list is reference data: reload it
            var first = await admin.GetAsync("/api/funds/99/performance?range=QTD", Ct);
            Assert.Equal(HttpStatusCode.NotFound, first.StatusCode);
            Assert.Null(first.Headers.ETag);

            // "No data" is cached too (cache-first): the second request's only connection is its own audit write.
            var me = (await admin.GetFromJsonAsync<Desk.Api.Auth.MeResponse>("/api/me", Ct))!.Email;
            await api.WaitForAuditAsync(a => a.UserName == me && a.Endpoint == "GET /api/me");
            var opened = api.ConnectionsOpened(api);
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/funds/99/performance?range=YTD", Ct)).StatusCode);
            await api.WaitForAuditAsync(a => a.UserName == me && a.Endpoint == "GET /api/funds/{fundId:int}/performance" && a.Status == 404, atLeast: 2);
            Assert.Equal(opened + 1, api.ConnectionsOpened(api));
        }
        finally
        {
            await Exec("DELETE FROM core.portfolio WHERE portfolio_id = 99; DELETE FROM core.fund WHERE fund_id = 99;");
            await ClearCache();
        }
    }

    [Fact]
    public async Task Repeat_is_a_cache_hit_and_if_none_match_is_304_and_the_payload_is_small()
    {
        var (client, _, _) = await api.SignedInAsync();
        var miss = await client.GetAsync("/api/funds/4/performance?range=ITD", Ct);
        Assert.True((await miss.Content.ReadAsByteArrayAsync(Ct)).Length < 5 * 1024, "README §6 P2: payload < 5 KB");
        var hit = await client.GetAsync("/api/funds/4/performance?range=ITD", Ct);
        Assert.Equal("HIT", hit.Headers.GetValues("X-Cache").Single());

        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/funds/4/performance?range=ITD");
        req.Headers.TryAddWithoutValidation("If-None-Match", miss.Headers.ETag!.ToString());
        Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(req, Ct)).StatusCode);
    }
}
