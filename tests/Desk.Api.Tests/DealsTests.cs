using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Desk.Api.Positions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>README §6 P4 / §11 on the seeded database (SEED=42, scale 0.1, as-of 2026-10-06).</summary>
[Collection(ApiCollection.Name)]
public sealed class DealsTests(PostgresApiFactory api)
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

    private static Dictionary<int, JsonElement> ById(JsonElement list) =>
        list.GetProperty("deals").EnumerateArray().ToDictionary(d => d.GetProperty("dealId").GetInt32());

    private static decimal? Dec(JsonElement e) => e.ValueKind == JsonValueKind.Null ? null : e.GetDecimal();

    /// <summary>(key, value) pairs from a two-column query; @asOf and @p (portfolio ids, default all 12) are bound.</summary>
    private async Task<Dictionary<int, decimal>> SqlAsync(string sql, int[]? portfolios = null)
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("asOf", PostgresApiFactory.AsOf);
        cmd.Parameters.AddWithValue("p", portfolios ?? Enumerable.Range(1, 12).ToArray());
        var rows = new Dictionary<int, decimal>();
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct)) rows[r.GetInt32(0)] = r.GetDecimal(1);
        return rows;
    }

    private async Task<int[]> IdsAsync(string sql)
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        var ids = new List<int>();
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct)) ids.Add(r.GetInt32(0));
        return [.. ids];
    }

    // ---------- README §6 P4 acceptance: every deal appears, zero-bond deals with blank bond columns ----------

    [Fact]
    public async Task Every_deal_is_listed_once_and_zero_bond_deals_have_blank_bond_columns()
    {
        var (client, _, _) = await api.SignedInAsync();
        var list = await JsonAsync(await client.GetAsync("/api/deals", Ct));
        Assert.Equal("2026-10-06", list.GetProperty("asOf").GetString());
        var deals = list.GetProperty("deals").EnumerateArray().ToArray();

        var all = await IdsAsync("SELECT deal_id FROM core.deal ORDER BY deal_id");
        Assert.Equal(all, deals.Select(d => d.GetProperty("dealId").GetInt32()));

        var zeroBond = await IdsAsync("SELECT deal_id FROM core.deal d WHERE NOT EXISTS (SELECT 1 FROM core.bond b WHERE b.deal_id = d.deal_id) ORDER BY 1");
        Assert.True(zeroBond.Length >= 3, "README §5.2: the seed has at least 3 deals with zero bonds");
        var byId = deals.ToDictionary(d => d.GetProperty("dealId").GetInt32());
        foreach (var id in zeroBond)
        {
            var d = byId[id];
            Assert.Equal(0, d.GetProperty("bondCount").GetInt32());
            foreach (var col in new[] { "currentBalance", "repBondId", "repClass", "repCusip", "repCurrentBalance", "repCouponType", "repCoupon", "repRating", "positions", "heldFace", "heldMarketValue" })
                Assert.Equal(JsonValueKind.Null, d.GetProperty(col).ValueKind);
            Assert.False(string.IsNullOrEmpty(d.GetProperty("name").GetString()));
        }
    }

    [Fact]
    public async Task The_representative_bond_is_the_most_senior_then_lowest_id()
    {
        var (client, _, _) = await api.SignedInAsync();
        var byId = ById(await JsonAsync(await client.GetAsync("/api/deals", Ct)));
        // Independent of LATERAL: ROW_NUMBER over the same order (ADR-0013's alternative), every deal with bonds.
        var expected = await SqlAsync(
            "SELECT deal_id, bond_id::numeric FROM (SELECT deal_id, bond_id, row_number() OVER (PARTITION BY deal_id ORDER BY seniority_rank, bond_id) rn "
            + "FROM core.bond) x WHERE rn = 1");
        Assert.Equal(expected.Count, byId.Values.Count(d => d.GetProperty("repBondId").ValueKind != JsonValueKind.Null));
        Assert.All(expected, e => Assert.Equal((int)e.Value, byId[e.Key].GetProperty("repBondId").GetInt32()));
    }

    // ---------- README §6 P4 acceptance: deal-level totals don't double count ----------

    [Fact]
    public async Task Deal_totals_are_counted_once_per_bond_and_once_per_position()
    {
        var (client, _, _) = await api.SignedInAsync();
        var byId = ById(await JsonAsync(await client.GetAsync("/api/deals", Ct)));

        var balance = await SqlAsync("SELECT deal_id, sum(current_balance) FROM core.bond GROUP BY deal_id");
        var held = await SqlAsync("SELECT deal_id, sum(market_value) FROM core.position_snapshot WHERE as_of_date = @asOf AND portfolio_id = ANY(@p) GROUP BY deal_id");
        var original = await SqlAsync("SELECT deal_id, original_balance FROM core.deal");
        Assert.All(balance, b => Assert.Equal(b.Value, Dec(byId[b.Key].GetProperty("currentBalance"))));
        Assert.All(held, h => Assert.Equal(h.Value, Dec(byId[h.Key].GetProperty("heldMarketValue"))));
        Assert.All(original, o => Assert.Equal(o.Value, Dec(byId[o.Key].GetProperty("originalBalance"))));
        Assert.Equal(held.Values.Sum(), byId.Values.Sum(d => Dec(d.GetProperty("heldMarketValue")) ?? 0));

        // The naive join (deal → bond → position) repeats a deal's held MV once per tranche and its balance once per
        // position: strictly more wherever a held deal has more than one bond, which the seed has.
        var naive = await SqlAsync(
            "SELECT d.deal_id, sum(p.market_value) FROM core.deal d JOIN core.bond b ON b.deal_id = d.deal_id "
            + "JOIN core.position_snapshot p ON p.deal_id = d.deal_id AND p.as_of_date = @asOf GROUP BY d.deal_id");
        var multiBond = await IdsAsync("SELECT deal_id FROM core.bond GROUP BY deal_id HAVING count(*) > 1");
        var doubled = held.Keys.Intersect(multiBond).Where(id => held[id] != 0).ToArray();
        Assert.NotEmpty(doubled);
        Assert.All(doubled, id => Assert.NotEqual(naive[id], Dec(byId[id].GetProperty("heldMarketValue"))));
    }

    [Fact]
    public async Task Detail_has_the_capital_structure_the_trend_and_the_positions()
    {
        var (client, _, _) = await api.SignedInAsync();
        var dealId = (await IdsAsync(
            "SELECT p.deal_id FROM core.position_snapshot p JOIN core.bond b ON b.deal_id = p.deal_id "
            + "WHERE p.as_of_date = '2026-10-06' GROUP BY p.deal_id HAVING count(DISTINCT b.bond_id) > 2 ORDER BY 1 LIMIT 1")).Single();
        var detail = await JsonAsync(await client.GetAsync($"/api/deals/{dealId}", Ct));

        Assert.Equal(dealId, detail.GetProperty("deal").GetProperty("dealId").GetInt32());
        var tranches = detail.GetProperty("tranches").EnumerateArray().ToArray();
        var expectedBonds = await IdsAsync($"SELECT bond_id FROM core.bond WHERE deal_id = {dealId} ORDER BY seniority_rank, bond_id");
        Assert.Equal(expectedBonds, tranches.Select(t => t.GetProperty("bondId").GetInt32()));

        var trend = detail.GetProperty("surveillance").EnumerateArray().Select(m => m.GetProperty("period").GetString()!).ToArray();
        Assert.InRange(trend.Length, 1, 12);
        Assert.Equal(trend.Order(StringComparer.Ordinal), trend);
        Assert.All(trend, p => Assert.True(string.CompareOrdinal(p, "2026-10-06") <= 0));
        var lastPeriods = await IdsAsync(
            $"SELECT count(*)::int FROM (SELECT 1 FROM surveillance.deal_remit WHERE deal_id = {dealId} AND period <= '2026-10-06' LIMIT 12) x");
        Assert.Equal(lastPeriods.Single(), trend.Length);

        var positions = detail.GetProperty("positions").EnumerateArray().ToArray();
        var mv = await SqlAsync($"SELECT deal_id, sum(market_value) FROM core.position_snapshot WHERE as_of_date = @asOf AND deal_id = {dealId} GROUP BY deal_id");
        Assert.Equal(mv[dealId], positions.Sum(p => Dec(p.GetProperty("marketValue")) ?? 0));
        Assert.All(positions, p => Assert.False(string.IsNullOrEmpty(p.GetProperty("portfolio").GetString())));
    }

    [Fact]
    public async Task A_zero_bond_deal_has_an_empty_capital_structure()
    {
        var (client, _, _) = await api.SignedInAsync();
        var id = (await IdsAsync("SELECT deal_id FROM core.deal d WHERE NOT EXISTS (SELECT 1 FROM core.bond b WHERE b.deal_id = d.deal_id) ORDER BY 1 LIMIT 1")).Single();
        var detail = await JsonAsync(await client.GetAsync($"/api/deals/{id}", Ct));
        Assert.Equal(0, detail.GetProperty("tranches").GetArrayLength());
        Assert.Equal(0, detail.GetProperty("positions").GetArrayLength());
        Assert.Equal(0, detail.GetProperty("surveillance").GetArrayLength());
    }

    [Theory]
    [InlineData("/api/deals/999999", HttpStatusCode.NotFound)]
    [InlineData("/api/deals?asOf=2020-01-01", HttpStatusCode.BadRequest)]
    [InlineData("/api/deals/1?asOf=2020-01-01", HttpStatusCode.BadRequest)]
    [InlineData("/api/deals?portfolioIds=1,x", HttpStatusCode.BadRequest)]
    [InlineData("/api/deals/1?portfolioIds=-1", HttpStatusCode.BadRequest)]
    public async Task Bad_requests_are_problems(string url, HttpStatusCode status)
    {
        var (client, _, _) = await api.SignedInAsync();
        var res = await client.GetAsync(url, Ct);
        Assert.Equal(status, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
        Assert.False(res.Headers.Contains("ETag"));
    }

    [Fact]
    public async Task Repeat_reads_are_cache_hits_and_revalidate_to_304()
    {
        var (client, _, _) = await api.SignedInAsync();
        foreach (var path in new[] { "/api/deals?portfolioIds=2,3", "/api/deals/1?portfolioIds=2,3" })
        {
            var miss = await client.GetAsync(path, Ct);
            var etag = miss.Headers.ETag!.ToString();
            Assert.Contains("total;dur=", miss.Headers.GetValues("Server-Timing").Single());
            var hit = await client.GetAsync(path.Replace("2,3", "3,2"), Ct); // same set, any order
            Assert.Equal("HIT", hit.Headers.GetValues("X-Cache").Single());
            Assert.Equal(etag, hit.Headers.ETag!.ToString());
            Assert.Equal(await miss.Content.ReadAsByteArrayAsync(Ct), await hit.Content.ReadAsByteArrayAsync(Ct));

            using var req = new HttpRequestMessage(HttpMethod.Get, path);
            req.Headers.TryAddWithoutValidation("If-None-Match", etag);
            Assert.Equal(HttpStatusCode.NotModified, (await client.SendAsync(req, Ct)).StatusCode);

            var other = await client.GetAsync(path.Replace("2,3", "2"), Ct);
            Assert.NotEqual(etag, other.Headers.ETag!.ToString());
        }
        var prior = await JsonAsync(await client.GetAsync("/api/deals?asOf=2026-10-05", Ct));
        Assert.Equal("2026-10-05", prior.GetProperty("asOf").GetString());
    }

    [Fact]
    public async Task Without_a_session_deals_are_401()
    {
        var client = PostgresApiFactory.NewClient(api);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/deals", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/deals/1", Ct)).StatusCode);
    }

    // ---------- ADR-0021: a stub provider decides what the SQL sees ----------

    [Fact]
    public async Task A_single_portfolio_sees_only_its_own_holdings()
    {
        var (host, client) = await HostWithAsync(P1);
        await using var _ = host;
        var byId = ById(await JsonAsync(await client.GetAsync("/api/deals", Ct)));
        Assert.Equal((await IdsAsync("SELECT deal_id FROM core.deal")).Length, byId.Count); // terms are desk-wide

        var mine = await SqlAsync("SELECT deal_id, sum(market_value) FROM core.position_snapshot WHERE as_of_date = @asOf AND portfolio_id = ANY(@p) GROUP BY deal_id", [P1]);
        var count = await SqlAsync("SELECT deal_id, count(*)::numeric FROM core.position_snapshot WHERE as_of_date = @asOf AND portfolio_id = ANY(@p) GROUP BY deal_id", [P1]);
        Assert.NotEmpty(mine);
        Assert.Equal(mine.Keys.Order(), byId.Where(d => d.Value.GetProperty("positions").ValueKind != JsonValueKind.Null).Select(d => d.Key).Order());
        Assert.All(mine, m => Assert.Equal(m.Value, Dec(byId[m.Key].GetProperty("heldMarketValue"))));
        Assert.All(count, c => Assert.Equal((int)c.Value, byId[c.Key].GetProperty("positions").GetInt32()));

        // The detail's positions are this portfolio's only, even for a deal other portfolios hold too.
        var shared = (await IdsAsync(
            "SELECT deal_id FROM core.position_snapshot WHERE as_of_date = '2026-10-06' GROUP BY deal_id "
            + "HAVING bool_or(portfolio_id = 1) AND bool_or(portfolio_id <> 1) ORDER BY 1 LIMIT 1")).Single();
        var detail = await JsonAsync(await client.GetAsync($"/api/deals/{shared}", Ct));
        var positions = detail.GetProperty("positions").EnumerateArray().ToArray();
        Assert.NotEmpty(positions);
        Assert.All(positions, p => Assert.Equal(P1, p.GetProperty("portfolioId").GetInt32()));
        Assert.Equal(mine[shared], positions.Sum(p => Dec(p.GetProperty("marketValue")) ?? 0));

        // Requesting a portfolio outside the entitlement doesn't widen the scope: it's dropped, leaving nothing held.
        var outside = await JsonAsync(await client.GetAsync("/api/deals?portfolioIds=2", Ct));
        Assert.All(outside.GetProperty("deals").EnumerateArray(), d => Assert.Equal(JsonValueKind.Null, d.GetProperty("heldMarketValue").ValueKind));
        Assert.Equal(0, (await JsonAsync(await client.GetAsync($"/api/deals/{shared}?portfolioIds=2", Ct))).GetProperty("positions").GetArrayLength());
    }

    [Fact]
    public async Task An_empty_entitlement_returns_zero_position_rows()
    {
        var (host, client) = await HostWithAsync();
        await using var _ = host;
        var deals = (await JsonAsync(await client.GetAsync("/api/deals", Ct))).GetProperty("deals").EnumerateArray().ToArray();
        Assert.NotEmpty(deals);
        Assert.All(deals, d =>
        {
            Assert.Equal(JsonValueKind.Null, d.GetProperty("positions").ValueKind);
            Assert.Equal(JsonValueKind.Null, d.GetProperty("heldFace").ValueKind);
            Assert.Equal(JsonValueKind.Null, d.GetProperty("heldMarketValue").ValueKind);
        });
        var held = (await IdsAsync("SELECT deal_id FROM core.position_snapshot WHERE as_of_date = '2026-10-06' ORDER BY 1 LIMIT 1")).Single();
        var detail = await JsonAsync(await client.GetAsync($"/api/deals/{held}", Ct));
        Assert.Equal(0, detail.GetProperty("positions").GetArrayLength());
        Assert.NotEqual(0, detail.GetProperty("tranches").GetArrayLength());
    }
}
