using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.Positions;
using Desk.Data.App.Migrations;
using Desk.Data.Catalog;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// #459 step 2 on a running app: production's catalog was seeded before #473, so its prices are |MV|-weighted until
/// <see cref="CatalogFaceWeightedPrices"/> runs. The app that is live then must serve the new weighting, and new
/// totals and ETags, within <see cref="MetaCache.Revalidate"/>: no restart, no cache clear, no reseed.
/// </summary>
/// <remarks>
/// This is the one test that changes a shared seeded row: it runs the migration's Down and Up on the fixture's
/// catalog. The api-postgres collection runs its tests one at a time, the Up in <c>finally</c> puts the seeded
/// (face-weighted) catalog back, and its host has its own clock and caches, so the shared host never sees the flip.
/// </remarks>
[Collection(ApiCollection.Name)]
public sealed class CatalogFlipTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Prices =
        [.. ColumnCatalog.PositionSnapshot.Where(c => c.Aggregation == Aggregation.WeightedByCurrentFace).Select(c => c.Name)];

    private const string Where = "as_of_date = DATE '2026-10-06' AND sector IN ('CLO', 'CMBS')";

    private static readonly object Body = new
    {
        columns = new[] { "deal_name", "price", "book_price", "spread_bp", "current_face" },
        endRow = 1,
        filterModel = new { sector = new { filterType = "set", values = new[] { "CLO", "CMBS" } } },
    };

    [Fact]
    public async Task Meta_totals_and_ETags_follow_the_catalog_flip_without_a_cache_clear()
    {
        var (host, clock) = api.WithOwnClock();
        await using var _ = host;
        var user = await api.CreateUserAsync();
        var client = PostgresApiFactory.NewClient(host);

        await ExecuteAsync(CatalogFaceWeightedPrices.SetAggregation(CatalogFaceWeightedPrices.MarketValueWeighted)); // as production is today
        try
        {
            await PostgresApiFactory.LoginAsync(client, user.Email!);
            var before = await AggregationsAsync(client);
            Assert.All(Prices, p => Assert.Equal(nameof(Aggregation.WeightedByMarketValue), before[p]));
            var (mvTag, mvSummary) = await QueryAsync(client);
            Assert.Equal(await WeightedAsync("price", "market_value"), mvSummary.GetProperty("price").GetDouble(), 6);
            Assert.Equal(await WeightedAsync("spread_bp", "market_value"), mvSummary.GetProperty("spread_bp").GetDouble(), 6);

            await ExecuteAsync(CatalogFaceWeightedPrices.SetAggregation(CatalogFaceWeightedPrices.FaceWeighted)); // the deploy's migrate step

            // Until the meta snapshot is re-read the app keeps serving the catalog it loaded, consistently.
            Assert.Equal(before, await AggregationsAsync(client));
            Assert.Equal(mvTag, (await QueryAsync(client)).ETag);

            // Within Revalidate, with no cache clear: the catalog, the Total-row averages and the ETag all change.
            clock.Advance(MetaCache.Revalidate);
            await PostgresApiFactory.LoginAsync(client, user.Email!); // the access token outlived its 5 minutes
            var after = await AggregationsAsync(client);
            Assert.Equal(ColumnCatalog.PositionSnapshot.ToDictionary(c => c.Name, c => c.Aggregation.ToString()), after);

            var res = await client.SendAsync(Query(), Ct);
            Assert.Equal("MISS", res.Headers.GetValues("X-Cache").Single());
            var faceTag = res.Headers.ETag!.ToString();
            Assert.NotEqual(mvTag, faceTag);
            var faceSummary = (await ReadJson(res)).GetProperty("summary");
            Assert.Equal(await WeightedAsync("price", "current_face"), faceSummary.GetProperty("price").GetDouble(), 6);
            Assert.Equal(await WeightedAsync("book_price", "current_face"), faceSummary.GetProperty("book_price").GetDouble(), 6);
            Assert.NotEqual(mvSummary.GetProperty("price").GetDouble(), faceSummary.GetProperty("price").GetDouble(), 6);
            // Columns the flip doesn't touch keep their totals.
            Assert.Equal(mvSummary.GetProperty("spread_bp").GetDouble(), faceSummary.GetProperty("spread_bp").GetDouble(), 9);
            Assert.Equal(mvSummary.GetProperty("current_face").GetDecimal(), faceSummary.GetProperty("current_face").GetDecimal());

            // An old ETag no longer matches: a browser holding the |MV|-weighted block gets the new one, not a 304.
            var conditional = Query();
            conditional.Headers.TryAddWithoutValidation("If-None-Match", mvTag);
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(conditional, Ct)).StatusCode);
        }
        finally
        {
            await ExecuteAsync(CatalogFaceWeightedPrices.SetAggregation(CatalogFaceWeightedPrices.FaceWeighted));
        }
    }

    private static HttpRequestMessage Query() =>
        new(HttpMethod.Post, "/api/positions/query") { Content = JsonContent.Create(Body) };

    private static async Task<(string ETag, JsonElement Summary)> QueryAsync(HttpClient client)
    {
        var res = await client.SendAsync(Query(), Ct);
        return (res.Headers.ETag!.ToString(), (await ReadJson(res)).GetProperty("summary"));
    }

    private static async Task<Dictionary<string, string>> AggregationsAsync(HttpClient client) =>
        (await client.GetFromJsonAsync<CatalogColumn[]>("/api/meta/columns", Ct))!.ToDictionary(c => c.Name, c => c.Aggregation);

    private static async Task<JsonElement> ReadJson(HttpResponseMessage res)
    {
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    // Independent of the builder: Σ(x·|w|) / Σ|w| over the rows that have x.
    private async Task<double> WeightedAsync(string column, string weight)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(
            $"SELECT sum({column} * abs({weight})::float8) / nullif(sum(abs({weight})::float8) FILTER (WHERE {column} IS NOT NULL), 0) " +
            $"FROM core.position_snapshot WHERE {Where}", conn);
        return (double)(await cmd.ExecuteScalarAsync(Ct))!;
    }

    private async Task ExecuteAsync(string sql)
    {
        await using var conn = await OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        return conn;
    }
}
