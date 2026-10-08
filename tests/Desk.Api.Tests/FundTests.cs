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
    public async Task A_fund_in_the_catalogue_but_without_performance_rows_is_404()
    {
        await using (var conn = new NpgsqlConnection(api.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            // Temporarily hide fund 3's history (one statement each way, inside this test).
            await using var hide = new NpgsqlCommand("CREATE TEMP TABLE keep AS SELECT * FROM core.fund_performance WHERE fund_id = 3; DELETE FROM core.fund_performance WHERE fund_id = 3;", conn);
            await hide.ExecuteNonQueryAsync(Ct);
            try
            {
                var (client, _, _) = await api.SignedInAsync();
                Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/funds/3/performance?range=QTD&nocache=1", Ct)).StatusCode);
            }
            finally
            {
                await using var restore = new NpgsqlCommand("INSERT INTO core.fund_performance SELECT * FROM keep;", conn);
                await restore.ExecuteNonQueryAsync(Ct);
            }
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
