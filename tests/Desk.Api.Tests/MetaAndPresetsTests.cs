using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.Positions;
using Desk.Data.App;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>README §8 meta endpoints and column presets (#47).</summary>
[Collection(ApiCollection.Name)]
public sealed class MetaAndPresetsTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HttpRequestMessage Send(HttpMethod method, string path, string? xsrf, object? body = null)
    {
        var req = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        if (xsrf is not null) req.Headers.Add("X-XSRF-TOKEN", xsrf);
        return req;
    }

    [Fact]
    public async Task As_of_dates_are_newest_first()
    {
        var (client, _, _) = await api.SignedInAsync();
        var res = await client.GetFromJsonAsync<AsOfResponse>("/api/meta/as-of", Ct);
        Assert.Equal(PostgresApiFactory.AsOf, res!.Latest);
        Assert.Equal([PostgresApiFactory.AsOf, new DateOnly(2026, 10, 5)], res.Dates);
    }

    [Fact]
    public async Task Meta_reports_a_miss_when_it_loads_and_a_hit_after()
    {
        var (admin, xsrf, _) = await api.SignedInAsync(Desk.Data.Auth.Roles.Admin);
        using var clear = Send(HttpMethod.Post, "/api/admin/cache/clear", xsrf);
        await admin.SendAsync(clear, Ct);
        var first = await admin.GetAsync("/api/meta/columns", Ct);
        Assert.Equal("MISS", first.Headers.GetValues("X-Cache").Single());
        Assert.DoesNotContain("db;dur=0.0,", first.Headers.GetValues("Server-Timing").Single());
        Assert.Equal("HIT", (await admin.GetAsync("/api/meta/columns", Ct)).Headers.GetValues("X-Cache").Single());
    }

    [Fact]
    public async Task Columns_come_from_app_column_catalog_and_match_the_code_catalog()
    {
        var (client, _, _) = await api.SignedInAsync();
        var res = await client.GetAsync("/api/meta/columns", Ct);
        Assert.Contains("total;dur=", res.Headers.GetValues("Server-Timing").Single());
        var columns = (await res.Content.ReadFromJsonAsync<CatalogColumn[]>(Ct))!;
        Assert.Equal(ColumnCatalog.PositionSnapshot.Select(c => (c.Name, c.Kind.ToString(), c.Aggregation.ToString(), c.Header)),
            columns.Select(c => (c.Name, c.Kind, c.Aggregation, c.Header)));
    }

    [Fact]
    public async Task Portfolios_lists_the_entitled_portfolios_with_their_fund()
    {
        var (client, _, _) = await api.SignedInAsync();
        var portfolios = (await client.GetFromJsonAsync<PortfolioResponse[]>("/api/meta/portfolios", Ct))!;
        Assert.Equal(12, portfolios.Length);
        Assert.All(portfolios, p => Assert.False(string.IsNullOrEmpty(p.FundName)));
    }

    [Fact]
    public async Task Presets_list_built_ins_then_save_update_and_delete_own()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var initial = (await client.GetFromJsonAsync<PresetResponse[]>("/api/presets/positions", Ct))!;
        Assert.Equal(["Risk", "Surveillance", "Scenarios", "All"], initial.Select(p => p.Name));
        Assert.All(initial, p => Assert.True(p.BuiltIn));
        Assert.Equal(BuiltInPresets.Risk.Count, initial[0].State.GetProperty("columns").GetArrayLength());

        var state = new { columns = new[] { "deal_name", "dv01" }, sort = new[] { new { colId = "dv01", sort = "desc" } } };
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = "My risk", state }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = "My risk", state = new { columns = new[] { "cs01" } } }), Ct)).StatusCode);

        var saved = (await client.GetFromJsonAsync<PresetResponse[]>("/api/presets/positions", Ct))!.Single(p => !p.BuiltIn);
        Assert.Equal("My risk", saved.Name);
        Assert.Equal("cs01", saved.State.GetProperty("columns")[0].GetString());
        Assert.NotNull(saved.UpdatedAt);

        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Send(HttpMethod.Delete, "/api/presets/positions?name=My%20risk", xsrf), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Send(HttpMethod.Delete, "/api/presets/positions?name=My%20risk", xsrf), Ct)).StatusCode);
    }

    [Fact]
    public async Task Every_preset_response_carries_server_timing()
    {
        // #44 / #130 N9-timing: Server-Timing on every data endpoint, success or not.
        var (client, xsrf, _) = await api.SignedInAsync();
        foreach (var req in new[]
        {
            Send(HttpMethod.Get, "/api/presets/positions", null),
            Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = "Timed", state = new { columns = new[] { "dv01" } } }),
            Send(HttpMethod.Delete, "/api/presets/positions?name=Timed", xsrf),
            Send(HttpMethod.Get, "/api/presets/nope", null),
        })
        {
            var res = await client.SendAsync(req, Ct);
            Assert.Matches(@"^db;dur=\d+\.\d, ser;dur=0\.0, total;dur=\d+\.\d$", res.Headers.GetValues("Server-Timing").Single());
        }
    }

    [Fact]
    public async Task Presets_are_per_user()
    {
        var (alice, xsrf, _) = await api.SignedInAsync();
        await alice.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = "Mine", state = new { columns = new[] { "dv01" } } }), Ct);

        var (bob, _, _) = await api.SignedInAsync();
        Assert.DoesNotContain((await bob.GetFromJsonAsync<PresetResponse[]>("/api/presets/positions", Ct))!, p => p.Name == "Mine");
    }

    [Theory]
    [InlineData("""{"name":"","state":{}}""")]
    [InlineData("""{"name":"Risk","state":{}}""")]
    [InlineData("""{"state":{}}""")]
    [InlineData("""{"name":"x","state":[1,2]}""")]
    [InlineData("""{"name":"x","state":"text"}""")]
    [InlineData("""{"name":"x","state":{"a":"\u0000"}}""")]
    public async Task Invalid_presets_are_400(string json)
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var req = new HttpRequestMessage(HttpMethod.Put, "/api/presets/positions") { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        req.Headers.Add("X-XSRF-TOKEN", xsrf);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(req, Ct)).StatusCode);
    }

    [Fact]
    public async Task Oversized_state_names_and_too_many_presets_are_refused()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        var big = new { name = "big", state = new { blob = new string('x', MetaEndpoints.MaxPresetStateBytes) } };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, big), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = new string('n', 65), state = new { } }), Ct)).StatusCode);

        for (var i = 0; i < PresetRepository.MaxPresetsPerPage; i++)
            Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = $"p{i}", state = new { } }), Ct)).StatusCode);
        var over = await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = "one too many", state = new { } }), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, over.StatusCode);
        // Replacing an existing one is still fine at the limit.
        Assert.Equal(HttpStatusCode.NoContent, (await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = "p0", state = new { a = 1 } }), Ct)).StatusCode);
    }

    [Fact]
    public async Task Unknown_pages_are_404_and_writes_need_antiforgery()
    {
        var (client, xsrf, _) = await api.SignedInAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/presets/funds", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Send(HttpMethod.Put, "/api/presets/funds", xsrf, new { name = "x", state = new { } }), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Send(HttpMethod.Delete, "/api/presets/positions", xsrf), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Send(HttpMethod.Delete, "/api/presets/funds?name=x", xsrf), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", null, new { name = "x", state = new { } }), Ct)).StatusCode);
    }

    [Theory]
    [InlineData(false, HttpStatusCode.NoContent)] // lost the insert race: the winner's row is updated instead
    [InlineData(true, HttpStatusCode.Conflict)]   // ...and that row was deleted again before the update: say so
    public async Task A_save_that_loses_the_unique_index_race_updates_or_reports_a_conflict(bool deleteBeforeUpdate, HttpStatusCode expected)
    {
        var race = new PresetRace(api.ConnectionString, deleteBeforeUpdate);
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(race)));
        var user = await api.CreateUserAsync();
        race.UserId = user.Id;
        var client = PostgresApiFactory.NewClient(host);
        var xsrf = await PostgresApiFactory.LoginAsync(client, user.Email!);

        var res = await client.SendAsync(Send(HttpMethod.Put, "/api/presets/positions", xsrf, new { name = "race", state = new { mine = true } }), Ct);
        Assert.Equal(expected, res.StatusCode);
        Assert.True(race.Raced);
    }

    /// <summary>
    /// Just before EF inserts a preset, another "request" inserts the same name on its own connection, so the
    /// insert hits the unique index every time. Optionally deletes that row again before the follow-up update.
    /// </summary>
    private sealed class PresetRace(string connectionString, bool deleteBeforeUpdate) : DbCommandInterceptor
    {
        public Guid UserId { get; set; }
        public bool Raced { get; private set; }

        public override async ValueTask<InterceptionResult<System.Data.Common.DbDataReader>> ReaderExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<System.Data.Common.DbDataReader> result, CancellationToken ct = default)
        {
            if (!Raced && command.CommandText.Contains("INSERT INTO app.preset", StringComparison.Ordinal))
            {
                Raced = true;
                await ExecAsync($"INSERT INTO app.preset (user_id, page, name, state, updated_at) VALUES ('{UserId}', 'positions', 'race', '{{}}', now())", ct);
            }
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            System.Data.Common.DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (Raced && deleteBeforeUpdate && command.CommandText.StartsWith("UPDATE app.preset", StringComparison.Ordinal))
                await ExecAsync($"DELETE FROM app.preset WHERE user_id = '{UserId}' AND name = 'race'", ct);
            return result;
        }

        private async Task ExecAsync(string sql, CancellationToken ct)
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = new NpgsqlCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);
        }
    }

    [Fact]
    public async Task Before_the_first_seed_data_endpoints_say_so_with_503()
    {
        // A migrated but never-seeded database in the same container.
        var empty = new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = $"empty_{Guid.NewGuid():N}" }.ConnectionString;
        await using (var conn = new NpgsqlConnection(api.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{new NpgsqlConnectionStringBuilder(empty).Database}\"", conn);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                         .UseNpgsql(empty, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options))
            await db.Database.MigrateAsync(Ct);

        // The repository on its own reports the empty state.
        var emptyRepo = new MetaRepository(
            new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<AppDbContext>(new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(empty, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options),
            new Desk.Data.Sources.DataSourceRegistry(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = empty }).Build(), new Desk.Data.DbConnectionCounter()));
        Assert.Equal("empty", await emptyRepo.DataVersionAsync(Ct));
        // An empty snapshot is retried within seconds, so a seed into a running API is picked up.
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero));
        var emptyCache = new MetaCache(emptyRepo, time);
        var none = await emptyCache.GetAsync(Ct);
        Assert.False(none.HasData);
        time.Advance(MetaCache.RetryEmpty);
        Assert.NotSame(none, await emptyCache.GetAsync(Ct));
        Assert.Empty(await emptyRepo.CatalogAsync(Ct));
        Assert.Empty(await emptyRepo.AsOfDatesAsync(Ct));

        var user = await api.CreateUserAsync();
        var cookie = (await PostgresApiFactory.PostLoginAsync(api.NewClient(), user.Email!)).Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(Auth.AuthSetup.CookieName + "=", StringComparison.Ordinal)).Split(';')[0];

        // Sessions are validated against the users table, so keep App on the seeded DB and point Core at the empty one.
        await using var host = api.WithSettings(("DATABASE_URL", empty), ("ConnectionStrings:Core", empty));
        var client = PostgresApiFactory.NewClient(host);
        using var asOf = new HttpRequestMessage(HttpMethod.Get, "/api/meta/as-of");
        asOf.Headers.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.SendAsync(asOf, Ct)).StatusCode);

        var xsrf = PostgresApiFactory.XsrfToken(await SendWithCookie(client, cookie, HttpMethod.Get, "/api/auth/antiforgery"))!;
        var query = await SendWithCookie(client, cookie, HttpMethod.Post, "/api/positions/query", xsrf, new { });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, query.StatusCode);
        Assert.Equal("No data loaded", (await query.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("title").GetString());
    }

    /// <summary>Captures log levels (the meta cache logs a count, never the names themselves).</summary>
    private sealed class Levels : Microsoft.Extensions.Logging.ILogger<MetaCache>
    {
        public List<(Microsoft.Extensions.Logging.LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    [Fact]
    public async Task A_catalog_name_that_is_not_snake_case_keeps_the_grid_unavailable()
    {
        // #130 N2: catalog names become quoted SQL identifiers. A separate database, so the seeded catalog is untouched.
        var other = new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = $"catalog_{Guid.NewGuid():N}" }.ConnectionString;
        await using (var conn = new NpgsqlConnection(api.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{new NpgsqlConnectionStringBuilder(other).Database}\"", conn);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(other, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
            db.ColumnCatalog.AddRange(
                new ColumnCatalogEntry { Name = "position_id", Ordinal = 1, Group = "keys", Kind = "Key", Aggregation = "None", Header = "Position" },
                new ColumnCatalogEntry { Name = "deal\" OR 1=1 --", Ordinal = 2, Group = "keys", Kind = "Text", Aggregation = "None", Header = "Deal" },
                // A trailing newline must not pass: the check anchors at \z, not $.
                new ColumnCatalogEntry { Name = "sector\n", Ordinal = 3, Group = "keys", Kind = "Text", Aggregation = "None", Header = "Sector" });
            await db.SaveChangesAsync(Ct);
        }
        var repo = new MetaRepository(
            new Microsoft.EntityFrameworkCore.Infrastructure.PooledDbContextFactory<AppDbContext>(options),
            new Desk.Data.Sources.DataSourceRegistry(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = other }).Build(), new Desk.Data.DbConnectionCounter()));
        var log = new Levels();
        var snapshot = await new MetaCache(repo, new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow), log).GetAsync(Ct);

        Assert.Null(snapshot.Normalizer);
        Assert.False(snapshot.HasData);
        var (level, message) = Assert.Single(log.Entries);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Error, level);
        Assert.Contains("has 2 names", message);
        Assert.DoesNotContain("OR 1=1", message);
        // The grid says why it's unavailable instead of claiming the database isn't seeded.
        var problem = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult>(PositionsEndpoints.Unavailable(snapshot, new GridRequest()));
        Assert.Equal(503, problem.StatusCode);
        Assert.Equal("Column catalog invalid", problem.ProblemDetails.Title);
    }

    private static Task<HttpResponseMessage> SendWithCookie(HttpClient client, string cookie, HttpMethod method, string path, string? xsrf = null, object? body = null)
    {
        var req = Send(method, path, xsrf, body);
        req.Headers.Add("Cookie", cookie);
        return client.SendAsync(req, Ct);
    }
}
