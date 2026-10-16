using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.Positions;
using Desk.Data.Grid;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Api.Tests;

/// <summary>
/// README §7.2 cache-first, measured in ADR-0014 (#67): a healthy demo session reads the database only on first views.
/// Every page's requests, twice, on a host of its own (its own caches and connection counter).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class FreeTierGuardrailTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Sources = ["core", "market", "surveillance", "pricing", "reference"];

    /// <summary>What the SPA asks for on a first view of each page (P1 to P4), in the order it asks.</summary>
    private static async Task<List<HttpResponseMessage>> ViewEveryPageAsync(HttpClient client, int dealId)
    {
        var responses = new List<HttpResponseMessage>
        {
            await client.GetAsync("/api/me", Ct),
            await client.GetAsync("/api/meta/as-of", Ct),
            await client.GetAsync("/api/meta/columns", Ct),
            await client.GetAsync("/api/meta/portfolios", Ct),
            await client.GetAsync("/api/presets/positions", Ct),
            await client.PostAsJsonAsync("/api/positions/query", new { columns = BuiltInPresets.Risk }, Ct),
            await client.GetAsync("/api/funds/1/performance", Ct),
        };
        foreach (var source in Sources)
            responses.Add(await client.GetAsync($"/api/insights/{source}", Ct));
        responses.Add(await client.GetAsync("/api/deals", Ct));
        responses.Add(await client.GetAsync($"/api/deals/{dealId}", Ct));
        return responses;
    }

    [Fact]
    public async Task A_demo_session_reads_the_database_only_on_first_views()
    {
        // The audit writer would open its own connection once a batch is due; keep it out of the count.
        await using var host = api.WithSettings(("AUDIT_FLUSH_SECONDS", "3600"));
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, user.Email!);
        var dealId = (await client.GetFromJsonAsync<JsonElement>("/api/deals", Ct)).GetProperty("deals").EnumerateArray().First().GetProperty("dealId").GetInt32();

        var first = await ViewEveryPageAsync(client, dealId);
        Assert.All(first, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var afterFirst = api.ConnectionsOpened(host);

        var repeat = await ViewEveryPageAsync(client, dealId);
        Assert.All(repeat, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        // Every data response says it came from the cache; /api/me reads only the token.
        Assert.All(repeat.Where(r => r.RequestMessage!.RequestUri!.AbsolutePath != "/api/me"),
            r => Assert.Equal("HIT", r.Headers.GetValues("X-Cache").Single()));
        Assert.Equal(afterFirst, api.ConnectionsOpened(host));
    }

    [Fact]
    public async Task Presets_are_cached_per_user_until_that_user_saves_or_deletes_one()
    {
        await using var host = api.WithSettings();
        var (alice, bob) = (PostgresApiFactory.NewClient(host), PostgresApiFactory.NewClient(host));
        await PostgresApiFactory.LoginAsync(alice, (await api.CreateUserAsync()).Email!);
        await PostgresApiFactory.LoginAsync(bob, (await api.CreateUserAsync()).Email!);

        Assert.Equal("MISS", await CacheOfListAsync(alice));
        Assert.Equal("HIT", await CacheOfListAsync(alice));
        Assert.Equal("MISS", await CacheOfListAsync(bob)); // another user's list is never served from Alice's entry

        var put = new HttpRequestMessage(HttpMethod.Put, "/api/presets/positions") { Content = JsonContent.Create(new { name = "Mine", state = new { columns = new[] { "dv01" } } }) };
        Assert.Equal(HttpStatusCode.NoContent, (await alice.SendAsync(put, Ct)).StatusCode);
        Assert.Equal("HIT", await CacheOfListAsync(bob)); // Alice's save doesn't drop Bob's entry
        var reread = await alice.GetAsync("/api/presets/positions", Ct);
        Assert.Equal("MISS", reread.Headers.GetValues("X-Cache").Single());
        Assert.Contains((await reread.Content.ReadFromJsonAsync<PresetResponse[]>(Ct))!, p => p.Name == "Mine");
        Assert.Equal("HIT", await CacheOfListAsync(alice));

        Assert.Equal(HttpStatusCode.NoContent, (await alice.SendAsync(new HttpRequestMessage(HttpMethod.Delete, "/api/presets/positions?name=Mine"), Ct)).StatusCode);
        var afterDelete = await alice.GetAsync("/api/presets/positions", Ct);
        Assert.Equal("MISS", afterDelete.Headers.GetValues("X-Cache").Single());
        Assert.DoesNotContain((await afterDelete.Content.ReadFromJsonAsync<PresetResponse[]>(Ct))!, p => p.Name == "Mine");
    }

    private static async Task<string> CacheOfListAsync(HttpClient client) =>
        (await client.GetAsync("/api/presets/positions", Ct)).Headers.GetValues("X-Cache").Single();

    [Fact]
    public void A_list_read_before_a_save_is_not_kept_after_it()
    {
        // The GET read the database, then a save landed before it stored the list: the stale list must not be served.
        using var cache = new PositionsCache(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System);
        var presets = new PresetListCache(cache);
        var user = Guid.NewGuid();
        var version = presets.Version(user, "positions");
        presets.Invalidate(user, "positions");
        presets.Store(user, "positions", [1, 2, 3], version);
        Assert.False(presets.TryGet(user, "positions", out _));

        presets.Store(user, "positions", [4], presets.Version(user, "positions"));
        Assert.True(presets.TryGet(user, "positions", out var bytes));
        Assert.Equal([4], bytes);
        presets.Invalidate(Guid.NewGuid(), "positions"); // another user's save: no effect, and no entry needed
        Assert.True(presets.TryGet(user, "positions", out _));
    }

    [Fact]
    public async Task A_cached_list_is_served_without_an_audit_row_to_mark()
    {
        // Outside the pipeline (no AuditMiddleware) there is no audit feature: the hit is still served and timed.
        using var cache = new PositionsCache(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), TimeProvider.System);
        var presets = new PresetListCache(cache);
        var user = Guid.NewGuid();
        presets.Store(user, "positions", "[]"u8.ToArray(), presets.Version(user, "positions"));
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, user.ToString())], "test")),
        };
        var result = await MetaEndpoints.ListPresetsAsync("positions", http, null!, presets, Ct);
        Assert.Equal("[]"u8.ToArray(), Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.FileContentHttpResult>(result).FileContents.ToArray());
        Assert.Equal("HIT", http.Response.Headers["X-Cache"].ToString());
    }

    [Fact]
    public async Task Meta_and_preset_reads_record_their_cache_status_in_the_audit()
    {
        // The Usage page's cache hit ratio (README §7.2) counts every cached read, not only the grid's.
        var (client, _, user) = await api.SignedInAsync();
        foreach (var _ in new[] { 1, 2 })
        {
            await client.GetAsync("/api/meta/columns", Ct);
            await client.GetAsync("/api/presets/positions", Ct);
        }
        var audit = await api.WaitForAuditAsync(a => a.UserName == user.Email && a.Endpoint is "GET /api/meta/columns" or "GET /api/presets/{page}", atLeast: 4);
        Assert.All(audit, a => Assert.Contains(a.Cache, new[] { "HIT", "MISS" }));
        Assert.Contains(audit, a => a.Endpoint == "GET /api/presets/{page}" && a.Cache == "MISS");
        Assert.Contains(audit, a => a.Endpoint == "GET /api/presets/{page}" && a.Cache == "HIT");
    }
}
