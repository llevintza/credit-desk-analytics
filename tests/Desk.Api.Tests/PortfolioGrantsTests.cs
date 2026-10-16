using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.Positions;
using Desk.Data.App;
using Desk.Data.Auth;
using Desk.Data.Grid;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// ADR-0021 R4: <c>ENTITLEMENTS_MODE=grants</c> end to end, with real grants in <c>app.portfolio_grant</c> loaded into the
/// sign-in claims (seed 42, scale 0.1). X = {p1}, Y = {p2}, an admin (E1), a user with no grants (E2) and a partial grant.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PortfolioGrantsTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const int P1 = 1;
    private const int P2 = 2;

    private WebApplicationFactory<Program> GrantsHost() => api.WithSettings((EntitlementsMode.ConfigKey, "grants"));

    private async Task GrantAsync(DeskUser user, params int[] portfolios)
    {
        await using var db = api.NewContext();
        db.PortfolioGrants.AddRange(portfolios.Select(p => new PortfolioGrant { UserId = user.Id, PortfolioId = p, GrantedAt = api.Time.GetUtcNow(), GrantedBy = "test" }));
        await db.SaveChangesAsync(Ct);
    }

    /// <summary>A new account of <paramref name="role"/> with <paramref name="portfolios"/> granted, signed in on <paramref name="host"/>.</summary>
    private async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> host, string role, params int[] portfolios)
    {
        var user = await api.CreateUserAsync(role);
        await GrantAsync(user, portfolios);
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, user.Email!);
        return client;
    }

    private static HttpRequestMessage Post(string path, object body, string? ifNoneMatch = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        if (ifNoneMatch is not null) req.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        return req;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task<T> ScalarAsync<T>(string sql, int[] portfolios)
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("p", portfolios);
        cmd.Parameters.AddWithValue("asof", PostgresApiFactory.AsOf);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private const string CountSql = "SELECT count(*) FROM core.position_snapshot WHERE portfolio_id = ANY(@p) AND as_of_date = @asof";
    private const string SumSql = "SELECT sum(market_value) FROM core.position_snapshot WHERE portfolio_id = ANY(@p) AND as_of_date = @asof";

    private static object Block(int start) => new { columns = new[] { "portfolio_id", "market_value" }, startRow = start, endRow = start + GridQueryNormalizer.MaxBlockRows };

    /// <summary>The portfolio column of every block, and the first block (rowCount, summary).</summary>
    private static async Task<(int[] Portfolios, JsonElement First)> AllBlocksAsync(HttpClient client)
    {
        var first = await JsonAsync(await client.SendAsync(Post("/api/positions/query", Block(0)), Ct));
        var rows = first.GetProperty("rowCount").GetInt32();
        var portfolios = new List<int>();
        for (var start = 0; start < rows; start += GridQueryNormalizer.MaxBlockRows)
        {
            var block = start == 0 ? first : await JsonAsync(await client.SendAsync(Post("/api/positions/query", Block(start)), Ct));
            portfolios.AddRange(block.GetProperty("data")[1].EnumerateArray().Select(v => v.GetInt32()));
        }
        return ([.. portfolios], first);
    }

    private static async Task<int[]> MetaPortfoliosAsync(HttpClient client) =>
        [.. (await JsonAsync(await client.GetAsync("/api/meta/portfolios", Ct))).EnumerateArray().Select(p => p.GetProperty("portfolioId").GetInt32())];

    private static async Task<string[]> ExportLinesAsync(HttpClient client)
    {
        var res = await client.SendAsync(Post("/api/positions/export", new { columns = new[] { "portfolio_id" } }), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return [.. (await res.Content.ReadAsStringAsync(Ct)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];
    }

    private async Task<int[]> AllPortfolioIdsAsync()
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT portfolio_id FROM core.portfolio ORDER BY 1", conn);
        await using var reader = await cmd.ExecuteReaderAsync(Ct);
        var ids = new List<int>();
        while (await reader.ReadAsync(Ct)) ids.Add(reader.GetInt32(0));
        return [.. ids];
    }

    [Fact]
    public async Task A_granted_viewer_sees_only_their_portfolio_on_query_export_and_meta()
    {
        await using var host = GrantsHost();
        var x = await SignedInAsync(host, Roles.Viewer, P1);

        var (portfolios, doc) = await AllBlocksAsync(x);
        var count = (int)await ScalarAsync<long>(CountSql, [P1]);
        Assert.True(count > 0, "the seed must give portfolio 1 positions for this test to mean anything");
        Assert.Equal(count, portfolios.Length);
        Assert.All(portfolios, p => Assert.Equal(P1, p));
        Assert.Equal(count, doc.GetProperty("rowCount").GetInt32());
        Assert.Equal(await ScalarAsync<decimal>(SumSql, [P1]), doc.GetProperty("summary").GetProperty("market_value").GetDecimal());

        var lines = await ExportLinesAsync(x);
        Assert.Equal("Position,Portfolio", lines[0]);
        Assert.Equal(count, lines.Length - 1);
        Assert.All(lines.Skip(1), line => Assert.Equal(P1.ToString(), line.Split(',')[1]));

        Assert.Equal(new[] { P1 }, await MetaPortfoliosAsync(x));
    }

    [Fact]
    public async Task A_user_with_no_grants_sees_nothing_anywhere()
    {
        // E2: fail closed.
        await using var host = GrantsHost();
        var none = await SignedInAsync(host, Roles.Viewer);

        var doc = await JsonAsync(await none.SendAsync(Post("/api/positions/query", Block(0)), Ct));
        Assert.Equal(0, doc.GetProperty("rowCount").GetInt32());
        Assert.All(doc.GetProperty("data").EnumerateArray(), column => Assert.Equal(0, column.GetArrayLength()));
        Assert.Equal(["Position,Portfolio"], await ExportLinesAsync(none));
        Assert.Empty(await MetaPortfoliosAsync(none));
    }

    [Fact]
    public async Task An_admin_sees_every_portfolio_without_grants()
    {
        // E1.
        await using var host = GrantsHost();
        var admin = await SignedInAsync(host, Roles.Admin);

        var all = await AllPortfolioIdsAsync();
        Assert.Equal(all, (await MetaPortfoliosAsync(admin)).Order());
        var doc = await JsonAsync(await admin.SendAsync(Post("/api/positions/query", Block(0)), Ct));
        Assert.Equal((int)await ScalarAsync<long>(CountSql, all), doc.GetProperty("rowCount").GetInt32());
        Assert.Equal((int)await ScalarAsync<long>(CountSql, all), (await ExportLinesAsync(admin)).Length - 1);
    }

    [Fact]
    public async Task A_request_is_silently_intersected_with_the_grant()
    {
        // E4: asking for p1 and p2 with only p2 granted returns p2's rows; asking for p1 alone returns none. Never 403.
        await using var host = GrantsHost();
        var y = await SignedInAsync(host, Roles.Viewer, P2);

        var both = await JsonAsync(await y.SendAsync(Post("/api/positions/query",
            new { columns = new[] { "portfolio_id" }, portfolioIds = new[] { P1, P2 }, startRow = 0, endRow = GridQueryNormalizer.MaxBlockRows }), Ct));
        Assert.Equal((int)await ScalarAsync<long>(CountSql, [P2]), both.GetProperty("rowCount").GetInt32());
        Assert.All(both.GetProperty("data")[1].EnumerateArray(), v => Assert.Equal(P2, v.GetInt32()));

        var outside = await JsonAsync(await y.SendAsync(Post("/api/positions/query", new { columns = new[] { "portfolio_id" }, portfolioIds = new[] { P1 } }), Ct));
        Assert.Equal(0, outside.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task A_partial_grant_sees_the_intersection_with_the_snapshot()
    {
        // A grant for a portfolio that isn't in the snapshot (lost in a reseed) drops out; the rest still applies.
        await using var host = GrantsHost();
        var gone = (await AllPortfolioIdsAsync()).Max() + 1000;
        var partial = await SignedInAsync(host, Roles.Viewer, P1, P2, gone);

        Assert.Equal([P1, P2], (await MetaPortfoliosAsync(partial)).Order());
        var (portfolios, doc) = await AllBlocksAsync(partial);
        Assert.Equal((int)await ScalarAsync<long>(CountSql, [P1, P2]), doc.GetProperty("rowCount").GetInt32());
        Assert.Equal([P1, P2], portfolios.Distinct().Order());
        Assert.Equal(await ScalarAsync<decimal>(SumSql, [P1, P2]), doc.GetProperty("summary").GetProperty("market_value").GetDecimal());
    }

    [Fact]
    public async Task Different_grants_never_share_a_cache_entry_or_a_304()
    {
        // One host, one cache: X warms it, then Y asks for the same view.
        await using var host = GrantsHost();
        var x = await SignedInAsync(host, Roles.Viewer, P1);
        var y = await SignedInAsync(host, Roles.Viewer, P2);

        var first = await x.SendAsync(Post("/api/positions/query", Block(0)), Ct);
        var etag = first.Headers.ETag!.ToString();
        await JsonAsync(first);
        Assert.Equal("HIT", (await x.SendAsync(Post("/api/positions/query", Block(0)), Ct)).Headers.GetValues("X-Cache").Single());

        var second = await y.SendAsync(Post("/api/positions/query", Block(0)), Ct);
        Assert.Equal("MISS", second.Headers.GetValues("X-Cache").Single());
        Assert.NotEqual(etag, second.Headers.ETag!.ToString());
        var secondDoc = await JsonAsync(second);
        Assert.All(secondDoc.GetProperty("data")[1].EnumerateArray(), v => Assert.Equal(P2, v.GetInt32()));
        Assert.Equal(await ScalarAsync<decimal>(SumSql, [P2]), secondDoc.GetProperty("summary").GetProperty("market_value").GetDecimal());

        // X's ETag replayed by Y is a full 200 for p2, never a 304.
        var replay = await y.SendAsync(Post("/api/positions/query", Block(0), ifNoneMatch: etag), Ct);
        var replayDoc = await JsonAsync(replay);
        Assert.All(replayDoc.GetProperty("data")[1].EnumerateArray(), v => Assert.Equal(P2, v.GetInt32()));
        Assert.Equal((int)await ScalarAsync<long>(CountSql, [P2]), replayDoc.GetProperty("rowCount").GetInt32());
    }

    [Fact]
    public async Task All_mode_ignores_grants_and_shows_every_portfolio()
    {
        // The default (ENTITLEMENTS_MODE unset) is today's behaviour, even for a user who has grants.
        var user = await api.CreateUserAsync();
        await GrantAsync(user, P1);
        var client = api.NewClient();
        await PostgresApiFactory.LoginAsync(client, user.Email!);

        var all = await AllPortfolioIdsAsync();
        Assert.Equal(all, (await MetaPortfoliosAsync(client)).Order());
        var doc = await JsonAsync(await client.SendAsync(Post("/api/positions/query", Block(0)), Ct));
        Assert.Equal((int)await ScalarAsync<long>(CountSql, all), doc.GetProperty("rowCount").GetInt32());
        Assert.IsType<AllPortfolios>(api.Services.GetRequiredService<IPortfolioEntitlements>());
    }

    [Fact]
    public async Task An_unknown_mode_fails_startup()
    {
        await using var host = api.WithSettings((EntitlementsMode.ConfigKey, "grant"));
        var e = Record.Exception(() => host.CreateClient());
        Assert.NotNull(e);
        var messages = string.Join(" | ", Unwrap(e).Select(x => x.Message));
        Assert.Contains(EntitlementsMode.ConfigKey, messages);

        static IEnumerable<Exception> Unwrap(Exception? e)
        {
            for (; e is not null; e = e.InnerException) yield return e;
        }
    }

    [Fact]
    public async Task A_revoke_or_a_new_grant_applies_at_the_next_refresh_without_ending_the_session()
    {
        // E5 RESIDUAL: the access token keeps its claims for up to 5 minutes; the refresh after that re-reads the grants.
        var (own, clock) = api.WithOwnClock();
        await using var _ = own;
        await using var host = own.WithWebHostBuilder(b => b.UseSetting(EntitlementsMode.ConfigKey, "grants"));
        var user = await api.CreateUserAsync(expiresAt: PostgresApiFactory.Start.AddDays(30));
        await GrantAsync(user, P1);
        var client = PostgresApiFactory.NewClient(host);
        var tokens = await PostgresApiFactory.LoginAsync(client, user.Email!);
        Assert.Equal(new[] { P1 }, await MetaPortfoliosAsync(client));
        string? stamp;
        await using (var db = api.NewContext())
            stamp = (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct)).SecurityStamp;

        await using (var db = api.NewContext())
        {
            await db.PortfolioGrants.Where(g => g.UserId == user.Id).ExecuteDeleteAsync(Ct);
            db.PortfolioGrants.Add(new PortfolioGrant { UserId = user.Id, PortfolioId = P2, GrantedAt = PostgresApiFactory.Start });
            await db.SaveChangesAsync(Ct);
        }
        // Within the access token's life the old claims still apply (the accepted lag).
        clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(new[] { P1 }, await MetaPortfoliosAsync(client));

        // Past 5 minutes the access token is gone; the refresh re-reads the grants and the session goes on.
        clock.Advance(TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/meta/portfolios", Ct)).StatusCode);
        var refreshed = await PostgresApiFactory.PostRefreshAsync(client, tokens.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, refreshed.StatusCode);
        PostgresApiFactory.UseToken(client, (await refreshed.Content.ReadFromJsonAsync<AccessTokenResponse>(Ct))!.AccessToken);
        Assert.Equal(new[] { P2 }, await MetaPortfoliosAsync(client));
        var doc = await JsonAsync(await client.SendAsync(Post("/api/positions/query", Block(0)), Ct));
        Assert.All(doc.GetProperty("data")[1].EnumerateArray(), v => Assert.Equal(P2, v.GetInt32()));

        // Changing grants never rotates the security stamp, so nothing signed the user out.
        await using (var db = api.NewContext())
            Assert.Equal(stamp, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct)).SecurityStamp);
    }

    [Fact]
    public async Task Deleting_an_account_deletes_its_grants()
    {
        var user = await api.CreateUserAsync();
        await GrantAsync(user, P1, P2);
        await using var db = api.NewContext();
        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(Ct);
        Assert.Equal(0, await db.PortfolioGrants.CountAsync(g => g.UserId == user.Id, Ct));
    }
}
