using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Desk.Api.Positions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// #126 (R121-F8): every positions read is scoped by <see cref="IPortfolioEntitlements"/>, end to end through the API,
/// with a stub provider in place of <see cref="AllPortfolios"/> (seed 42, scale 0.1, README §6 P1 / §8).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class EntitlementScopingTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const int P1 = 1;
    private const int P2 = 2;

    /// <summary>Grants exactly the portfolios set on it; switchable so one host (one cache) serves several grants.</summary>
    private sealed class StubEntitlements : IPortfolioEntitlements
    {
        public int[] Ids { get; set; } = [];
        public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta) => Ids;
    }

    private async Task<(WebApplicationFactory<Program> Host, HttpClient Client, string Xsrf)> HostAsync(StubEntitlements stub)
    {
        var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IPortfolioEntitlements>(stub)));
        var client = PostgresApiFactory.NewClient(host);
        var xsrf = await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        return (host, client, xsrf);
    }

    private static HttpRequestMessage Post(string path, string xsrf, object body, string? ifNoneMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        req.Headers.Add("X-XSRF-TOKEN", xsrf);
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return req;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task<T> ScalarAsync<T>(string sql, int portfolioId)
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("p", portfolioId);
        cmd.Parameters.AddWithValue("asof", PostgresApiFactory.AsOf);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private const string CountSql = "SELECT count(*) FROM core.position_snapshot WHERE portfolio_id = @p AND as_of_date = @asof";
    private const string SumSql = "SELECT sum(market_value) FROM core.position_snapshot WHERE portfolio_id = @p AND as_of_date = @asof";

    private static object Block(int start) => new { columns = new[] { "portfolio_id", "market_value" }, startRow = start, endRow = start + 500 };
    private static readonly object Body = Block(0);

    /// <summary>Every block of the query (500 rows each, the cap), concatenated: the portfolio column and the first block.</summary>
    private static async Task<(int[] Portfolios, JsonElement First)> AllBlocksAsync(HttpClient client, string xsrf)
    {
        var first = await JsonAsync(await client.SendAsync(Post("/api/positions/query", xsrf, Body), Ct));
        var rows = first.GetProperty("rowCount").GetInt32();
        var portfolios = new List<int>();
        for (var start = 0; start < rows; start += 500)
        {
            var block = start == 0 ? first : await JsonAsync(await client.SendAsync(Post("/api/positions/query", xsrf, Block(start)), Ct));
            portfolios.AddRange(block.GetProperty("data")[1].EnumerateArray().Select(v => v.GetInt32()));
        }
        return ([.. portfolios], first);
    }

    [Fact]
    public async Task Query_returns_only_entitled_rows_with_matching_count_and_summary()
    {
        var (host, client, xsrf) = await HostAsync(new StubEntitlements { Ids = [P1] });
        await using var _ = host;
        var (portfolios, doc) = await AllBlocksAsync(client, xsrf);

        Assert.Equal(["position_id", "portfolio_id", "market_value"], doc.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        var count = (int)await ScalarAsync<long>(CountSql, P1);
        Assert.True(count > 0, "the seed must give portfolio 1 positions for this test to mean anything");
        Assert.Equal(count, portfolios.Length);
        Assert.All(portfolios, p => Assert.Equal(P1, p));
        Assert.Equal(count, doc.GetProperty("rowCount").GetInt32());
        Assert.Equal(await ScalarAsync<decimal>(SumSql, P1), doc.GetProperty("summary").GetProperty("market_value").GetDecimal());
    }

    [Fact]
    public async Task Export_contains_only_entitled_rows()
    {
        var (host, client, xsrf) = await HostAsync(new StubEntitlements { Ids = [P1] });
        await using var _ = host;
        var res = await client.SendAsync(Post("/api/positions/export", xsrf, new { columns = new[] { "portfolio_id", "market_value" } }), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var lines = (await res.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("Position,Portfolio,Market value", lines[0].TrimEnd('\r'));
        Assert.Equal(await ScalarAsync<long>(CountSql, P1), lines.Length - 1);
        Assert.All(lines.Skip(1), line => Assert.Equal(P1.ToString(), line.Split(',')[1]));
    }

    [Fact]
    public async Task Meta_portfolios_lists_exactly_the_entitled_set()
    {
        var (host, client, _) = await HostAsync(new StubEntitlements { Ids = [P1] });
        await using var _ = host;
        var list = await JsonAsync(await client.GetAsync("/api/meta/portfolios", Ct));
        Assert.Equal([P1], list.EnumerateArray().Select(p => p.GetProperty("portfolioId").GetInt32()));
    }

    [Fact]
    public async Task An_empty_grant_sees_nothing_anywhere()
    {
        var (host, client, xsrf) = await HostAsync(new StubEntitlements { Ids = [] });
        await using var _ = host;

        var doc = await JsonAsync(await client.SendAsync(Post("/api/positions/query", xsrf, Body), Ct));
        Assert.Equal(0, doc.GetProperty("rowCount").GetInt32());
        Assert.All(doc.GetProperty("data").EnumerateArray(), column => Assert.Equal(0, column.GetArrayLength()));

        var export = await client.SendAsync(Post("/api/positions/export", xsrf, new { columns = new[] { "portfolio_id" } }), Ct);
        Assert.Equal(HttpStatusCode.OK, export.StatusCode);
        Assert.Equal(["Position,Portfolio"], (await export.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')));

        Assert.Equal(0, (await JsonAsync(await client.GetAsync("/api/meta/portfolios", Ct))).GetArrayLength());
    }

    [Fact]
    public async Task A_different_grant_never_shares_a_cache_entry_or_a_304()
    {
        // One host, one cache: only the grant changes between the two requests.
        var stub = new StubEntitlements { Ids = [P1] };
        var (host, client, xsrf) = await HostAsync(stub);
        await using var _ = host;

        var first = await client.SendAsync(Post("/api/positions/query", xsrf, Body), Ct);
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        var etag = first.Headers.ETag!.ToString();

        stub.Ids = [P2];
        var second = await client.SendAsync(Post("/api/positions/query", xsrf, Body), Ct);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.NotEqual(etag, second.Headers.ETag!.ToString());
        Assert.Equal("MISS", second.Headers.GetValues("X-Cache").Single());
        Assert.All((await JsonAsync(second)).GetProperty("data")[1].EnumerateArray(), v => Assert.Equal(P2, v.GetInt32()));

        // Portfolio 1's ETag, replayed under the portfolio 2 grant, is a full 200 for portfolio 2, never a 304.
        var replay = await client.SendAsync(Post("/api/positions/query", xsrf, Body, ifNoneMatch: etag), Ct);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.All((await JsonAsync(replay)).GetProperty("data")[1].EnumerateArray(), v => Assert.Equal(P2, v.GetInt32()));
    }

    [Fact]
    public async Task Requesting_an_unentitled_portfolio_is_silently_empty_not_403()
    {
        // ADR-0021 E4: requested ids are intersected with the grant.
        var (host, client, xsrf) = await HostAsync(new StubEntitlements { Ids = [P1] });
        await using var _ = host;
        var res = await client.SendAsync(Post("/api/positions/query", xsrf, new { columns = new[] { "portfolio_id" }, portfolioIds = new[] { P2 } }), Ct);
        var doc = await JsonAsync(res);
        Assert.Equal(0, doc.GetProperty("rowCount").GetInt32());
        Assert.All(doc.GetProperty("data").EnumerateArray(), column => Assert.Equal(0, column.GetArrayLength()));
    }
}
