using System.Net;
using System.Net.Http.Json;
using System.Net.ServerSentEvents;
using System.Security.Claims;
using System.Text.Json;
using Desk.Api.Limits;
using Desk.Api.Marks;
using Desk.Api.Positions;
using Desk.Data.Marks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// README §6 P6 / ADR-0015 (#71): the SSE mark stream and the overlay totals, on the seeded database (SEED=42, scale 0.1,
/// as-of 2026-10-06), scoped by a stub <see cref="IPortfolioEntitlements"/> (ADR-0021 E7).
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class MarksTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private const int P1 = 1;
    private static readonly MarkSimulator Sim = new(TimeSpan.FromSeconds(1));

    private sealed class StubEntitlements(params int[] ids) : IPortfolioEntitlements
    {
        public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta) => ids;
    }

    private static WebApplicationFactory<Program> Scoped(WebApplicationFactory<Program> host, int[] portfolios, params (string Key, string Value)[] settings) =>
        host.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureTestServices(s => s.AddSingleton<IPortfolioEntitlements>(new StubEntitlements(portfolios)));
        });

    private async Task<HttpClient> SignInAsync(WebApplicationFactory<Program> host)
    {
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        return client;
    }

    /// <summary>An open stream: its events as they arrive. Disposing it closes the connection.</summary>
    private sealed class Stream(HttpResponseMessage response, IAsyncEnumerator<SseItem<string>> events) : IAsyncDisposable
    {
        public HttpResponseMessage Response => response;

        public async Task<SseItem<string>?> NextAsync()
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            return await events.MoveNextAsync().AsTask().WaitAsync(timeout.Token) ? events.Current : null;
        }

        /// <summary>Reads until <paramref name="count"/> events of <paramref name="type"/> have arrived; also returns everything read.</summary>
        public async Task<List<SseItem<string>>> ReadAsync(int count, string type = "mark")
        {
            var read = new List<SseItem<string>>();
            while (read.Count(e => e.EventType == type) < count && await NextAsync() is { } e)
                read.Add(e);
            return read;
        }

        public async ValueTask DisposeAsync()
        {
            response.Dispose(); // closes the connection, which also ends a read still waiting
            try { await events.DisposeAsync(); }
            catch (NotSupportedException) { } // a read timed out mid-wait: the parser can't be disposed under it
        }
    }

    private static async Task<Stream> OpenAsync(HttpClient client, string? lastEventId = null, string query = "")
    {
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/marks/stream" + query);
        if (lastEventId is not null) req.Headers.TryAddWithoutValidation("Last-Event-ID", lastEventId);
        var res = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/event-stream", res.Content.Headers.ContentType!.MediaType);
        var body = await res.Content.ReadAsStreamAsync(Ct);
        return new Stream(res, SseParser.Create(body).EnumerateAsync(Ct).GetAsyncEnumerator(Ct));
    }

    private static JsonElement Data(SseItem<string> e) => JsonDocument.Parse(e.Data).RootElement.Clone();

    private async Task<HashSet<int>> HeldBondsAsync(params int[] portfolios)
    {
        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand(
            "SELECT DISTINCT bond_id FROM core.position_snapshot WHERE as_of_date = @d AND portfolio_id = ANY(@p) AND price IS NOT NULL", conn);
        cmd.Parameters.AddWithValue("d", PostgresApiFactory.AsOf);
        cmd.Parameters.AddWithValue("p", portfolios);
        var ids = new HashSet<int>();
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        while (await r.ReadAsync(Ct)) ids.Add(r.GetInt32(0));
        return ids;
    }

    [Fact]
    public async Task The_stream_says_hello_then_marks_every_scoped_bond_once_with_bond_id_seq_and_event_ts()
    {
        await using var host = Scoped(api, [P1]);
        var client = await SignInAsync(host);
        await using var stream = await OpenAsync(client);

        var hello = await stream.NextAsync();
        Assert.Equal("hello", hello!.Value.EventType);
        Assert.Equal(MarksEndpoints.Retry, hello.Value.ReconnectionInterval);
        var info = Data(hello.Value);
        var bonds = info.GetProperty("bonds").GetInt32();
        var now = Sim.TickAt(api.Time.GetUtcNow());
        Assert.Equal("2026-10-06", info.GetProperty("asOf").GetString());
        Assert.Equal(1000, info.GetProperty("tickMs").GetInt32());
        Assert.Equal(now, info.GetProperty("seq").GetInt64());
        Assert.Equal(api.Time.GetUtcNow().AddMinutes(5), info.GetProperty("endsAt").GetDateTimeOffset());

        var held = await HeldBondsAsync(P1);
        Assert.Equal(Math.Min(100, held.Count), bonds); // a subset: MARKS_MAX_BONDS (100) of the portfolio's bonds

        var marks = (await stream.ReadAsync(bonds)).Select(Data).ToList();
        Assert.Equal(bonds, marks.Count);
        Assert.Equal(bonds, marks.Select(m => m.GetProperty("bondId").GetInt32()).Distinct().Count());
        Assert.All(marks, m =>
        {
            Assert.Contains(m.GetProperty("bondId").GetInt32(), held); // never a bond outside the scope (ADR-0021)
            var seq = m.GetProperty("seq").GetInt64();
            Assert.InRange(seq, now - MarkSimulator.MaxPeriodTicks, now);
            Assert.Equal(Sim.TimeOf(seq), m.GetProperty("eventTs").GetDateTimeOffset());
            Assert.True(m.GetProperty("price").GetDouble() > 0);
        });
        // Oldest first, and each event's SSE id is its seq, so Last-Event-ID resumes from it.
        var seqs = marks.Select(m => m.GetProperty("seq").GetInt64()).ToList();
        Assert.Equal(seqs.Order(), seqs);
        Assert.Equal("no", stream.Response.Headers.GetValues("X-Accel-Buffering").Single());
    }

    [Fact]
    public async Task A_reconnect_with_Last_Event_ID_never_replays_a_mark_at_or_below_it()
    {
        await using var host = Scoped(api, [P1]);
        var client = await SignInAsync(host);
        var now = Sim.TickAt(api.Time.GetUtcNow());

        // Up to date: nothing to replay at all (the stale-event rule, server side).
        await using (var current = await OpenAsync(client, now.ToString()))
        {
            var bonds = Data((await current.NextAsync())!.Value).GetProperty("bonds").GetInt32();
            Assert.True(bonds > 0);
            using var quiet = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            quiet.CancelAfter(TimeSpan.FromMilliseconds(500));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => current.NextAsync().WaitAsync(quiet.Token));
        }

        // Five ticks behind: exactly the bonds that changed in those ticks, all newer than the id sent.
        var behind = now - 5;
        await using var stream = await OpenAsync(client, behind.ToString());
        var universe = await host.Services.GetRequiredService<MarkUniverses>().GetAsync(
            await host.Services.GetRequiredService<MetaCache>().GetAsync(Ct), [P1], Ct);
        var expected = universe.Bonds.Where(b => MarkSimulator.LastChange(b.BondId, now) > behind).Select(b => b.BondId).Order().ToList();
        Assert.NotEmpty(expected);
        var marks = (await stream.ReadAsync(expected.Count)).Where(e => e.EventType == "mark").Select(Data).ToList();
        Assert.Equal(expected, marks.Select(m => m.GetProperty("bondId").GetInt32()).Order());
        Assert.All(marks, m => Assert.True(m.GetProperty("seq").GetInt64() > behind));
    }

    [Fact]
    public async Task An_empty_grant_gets_hello_and_bye_and_no_marks()
    {
        await using var host = Scoped(api, []);
        var client = await SignInAsync(host);
        await using var stream = await OpenAsync(client);
        var events = new List<SseItem<string>>();
        while (await stream.NextAsync() is { } e) events.Add(e);
        Assert.Equal(["hello", "bye"], events.Select(e => e.EventType));
        Assert.Equal(0, Data(events[0]).GetProperty("bonds").GetInt32());
        Assert.Equal("empty", Data(events[1]).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Requested_portfolios_narrow_the_scope_and_unentitled_ones_are_dropped()
    {
        await using var host = Scoped(api, [P1]);
        var client = await SignInAsync(host);
        // Portfolio 2 isn't granted: dropped silently (never 403), so nothing is left to mark.
        await using (var other = await OpenAsync(client, query: "?portfolioIds=2"))
            Assert.Equal(0, Data((await other.NextAsync())!.Value).GetProperty("bonds").GetInt32());
        await using (var mine = await OpenAsync(client, query: "?portfolioIds=1,2"))
            Assert.True(Data((await mine.NextAsync())!.Value).GetProperty("bonds").GetInt32() > 0);
        var bad = await client.GetAsync("/api/marks/stream?portfolioIds=1;DROP", Ct);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [Fact]
    public async Task Ticks_bring_newer_marks_and_the_server_ends_the_stream_at_its_lifetime()
    {
        var (own, clock) = api.WithOwnClock();
        await using var _ = own;
        await using var host = Scoped(own, [P1], ("MARKS_STREAM_SECONDS", "30"));
        var client = await SignInAsync(host);
        await using var stream = await OpenAsync(client);
        var bonds = Data((await stream.NextAsync())!.Value).GetProperty("bonds").GetInt32();
        var first = (await stream.ReadAsync(bonds)).Select(Data).ToList();
        var start = Sim.TickAt(clock.GetUtcNow());

        // Twenty ticks later (one at a time, as the server waits on each): only changed bonds, each newer than before.
        var later = new List<JsonElement>();
        for (var i = 0; i < 20; i++)
        {
            var expected = (await host.Services.GetRequiredService<MarkUniverses>().GetAsync(
                await host.Services.GetRequiredService<MetaCache>().GetAsync(Ct), [P1], Ct)).Bonds
                .Count(b => MarkSimulator.LastChange(b.BondId, start + i + 1) == start + i + 1);
            clock.Advance(TimeSpan.FromSeconds(1));
            if (expected > 0) later.AddRange((await stream.ReadAsync(expected)).Select(Data));
        }
        Assert.NotEmpty(later);
        Assert.All(later, m => Assert.InRange(m.GetProperty("seq").GetInt64(), start + 1, start + 20));
        var lastSeen = first.ToDictionary(m => m.GetProperty("bondId").GetInt32(), m => m.GetProperty("seq").GetInt64());
        foreach (var m in later)
        {
            var bond = m.GetProperty("bondId").GetInt32();
            Assert.True(m.GetProperty("seq").GetInt64() > lastSeen[bond]);
            lastSeen[bond] = m.GetProperty("seq").GetInt64();
        }

        // At 30 s the server says bye and closes; the client decides whether to come back.
        clock.Advance(TimeSpan.FromSeconds(10));
        var tail = new List<SseItem<string>>();
        while (await stream.NextAsync() is { } e) tail.Add(e);
        Assert.Equal("bye", tail[^1].EventType);
        Assert.Equal("lifetime", Data(tail[^1]).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task An_open_stream_does_not_hold_its_owners_request_permit_or_a_database_permit()
    {
        // Real per-user limits (one request in flight) and a single database permit.
        await using var host = Scoped(api, [P1], ("RATE_LIMIT_PER_USER_CONCURRENCY", "1"), ("RATE_LIMIT_GLOBAL_CONCURRENCY", "1"));
        var client = await SignInAsync(host);
        await using var stream = await OpenAsync(client);
        Assert.Equal("hello", (await stream.NextAsync())!.Value.EventType);

        var query = await client.PostAsJsonAsync("/api/positions/query", new { columns = new[] { "deal_name" }, startRow = 0, endRow = 10 }, Ct);
        Assert.Equal(HttpStatusCode.OK, query.StatusCode);
    }

    [Fact]
    public async Task Streams_are_capped_per_user_and_in_total()
    {
        await using var host = Scoped(api, [P1], ("MARKS_STREAMS_PER_USER", "1"), ("MARKS_STREAMS_GLOBAL", "2"));
        var alice = await SignInAsync(host);
        await using var first = await OpenAsync(alice);
        var second = await alice.GetAsync("/api/marks/stream", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.NotNull(second.Headers.RetryAfter);

        var bob = await SignInAsync(host);
        await using var bobs = await OpenAsync(bob);
        var carol = await SignInAsync(host);
        var full = await carol.GetAsync("/api/marks/stream", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, full.StatusCode);
    }

    [Fact]
    public async Task The_start_up_read_waits_for_a_database_permit_and_gets_429_when_the_queue_is_full()
    {
        await using var host = Scoped(api, [P1], ("RATE_LIMIT_GLOBAL_CONCURRENCY", "1"), ("RATE_LIMIT_GLOBAL_QUEUE", "1"));
        var client = await SignInAsync(host);
        var permits = host.Services.GetRequiredService<DatabasePermits>();
        using var held = await permits.AcquireAsync(Ct);
        Assert.True(held.IsAcquired);
        var queued = permits.AcquireAsync(Ct); // takes the one queue place
        var res = await client.GetAsync("/api/marks/stream", HttpCompletionOption.ResponseHeadersRead, Ct);
        Assert.Equal(HttpStatusCode.TooManyRequests, res.StatusCode);
        Assert.Equal("1", res.Headers.GetValues("Retry-After").Single());
        held.Dispose();
        using var next = await queued;
        Assert.True(next.IsAcquired);
    }

    [Fact]
    public void The_shared_database_permits_never_go_idle_so_the_global_limiter_keeps_one_pool()
    {
        var permits = new DatabasePermits(LimitsOptions.From(new ConfigurationBuilder()
            .AddInMemoryCollection([KeyValuePair.Create("RATE_LIMIT_GLOBAL_CONCURRENCY", (string?)"1")]).Build()));
        Assert.Null(permits.Limiter.IdleDuration);
        using (var lease = permits.Limiter.AttemptAcquire())
        {
            Assert.True(lease.IsAcquired);
            Assert.Equal(0, permits.Limiter.GetStatistics()!.CurrentAvailablePermits);
            using var none = permits.Limiter.AttemptAcquire();
            Assert.False(none.IsAcquired);
        }
        Assert.Null(permits.Limiter.IdleDuration); // idle, but never says so
        Assert.Equal(1, permits.Limiter.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task The_stream_needs_a_session()
    {
        var res = await PostgresApiFactory.NewClient(api).GetAsync("/api/marks/stream", Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    private static object TotalsBody(string? asOf = null, string[]? columns = null) => new
    {
        asOf,
        columns = columns ?? ["deal_name", "price", "spread_bp", "market_value"],
        filterModel = new Dictionary<string, object> { ["sector"] = new { filterType = "set", values = new[] { "CLO", "RMBS" } } },
    };

    [Fact]
    public async Task Totals_are_the_scoped_filtered_weighted_averages_with_the_windows_marks_applied_and_cached()
    {
        await using var host = Scoped(api, [P1]);
        var client = await SignInAsync(host);
        var res = await client.PostAsJsonAsync("/api/marks/totals", TotalsBody(), Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("MISS", res.Headers.GetValues("X-Cache").Single());
        var totals = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement;

        var tick = Sim.TickAt(api.Time.GetUtcNow());
        var at = tick - tick % 15; // MARKS_TOTALS_SECONDS = 15
        Assert.Equal(at, totals.GetProperty("seq").GetInt64());
        Assert.Equal(Sim.TimeOf(at), totals.GetProperty("eventTs").GetDateTimeOffset());

        // The same numbers computed independently: each held bond's mark at the window's tick, weighted by |MV|.
        var universe = await host.Services.GetRequiredService<MarkUniverses>().GetAsync(
            await host.Services.GetRequiredService<MetaCache>().GetAsync(Ct), [P1], Ct);
        var marks = universe.Bonds.ToDictionary(b => b.BondId, b => Sim.MarkAt(b, at));
        double wp = 0, w = 0, ws = 0, wsd = 0;
        var rows = 0;
        await using (var conn = new NpgsqlConnection(api.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand(
                "SELECT bond_id, price, spread_bp, abs(market_value)::float8 FROM core.position_snapshot " +
                "WHERE as_of_date = @d AND portfolio_id = @p AND sector IN ('CLO', 'RMBS')", conn);
            cmd.Parameters.AddWithValue("d", PostgresApiFactory.AsOf);
            cmd.Parameters.AddWithValue("p", P1);
            await using var r = await cmd.ExecuteReaderAsync(Ct);
            while (await r.ReadAsync(Ct))
            {
                rows++;
                if (r.IsDBNull(3)) continue;
                var weight = r.GetDouble(3);
                marks.TryGetValue(r.GetInt32(0), out var mark);
                double? price = mark?.Price ?? (r.IsDBNull(1) ? null : r.GetDouble(1));
                double? spread = mark is not null ? mark.SpreadBp : r.IsDBNull(2) ? null : r.GetDouble(2);
                if (price is { } p) { wp += p * weight; w += weight; }
                if (spread is { } s) { ws += s * weight; wsd += weight; }
            }
        }
        Assert.True(universe.Bonds.Count > 0 && rows > 0);
        Assert.Equal(rows, totals.GetProperty("rowCount").GetInt32());
        Assert.Equal(wp / w, totals.GetProperty("summary").GetProperty("price").GetDouble(), 6);
        Assert.Equal(ws / wsd, totals.GetProperty("summary").GetProperty("spread_bp").GetDouble(), 6);

        // The same view in the same window: served from the cache.
        var again = await client.PostAsJsonAsync("/api/marks/totals", TotalsBody(), Ct);
        Assert.Equal("HIT", again.Headers.GetValues("X-Cache").Single());
        Assert.Equal(totals.GetRawText(), JsonDocument.Parse(await again.Content.ReadAsStringAsync(Ct)).RootElement.GetRawText());
    }

    [Fact]
    public async Task Totals_for_an_empty_grant_are_empty_and_never_NaN()
    {
        await using var host = Scoped(api, []);
        var client = await SignInAsync(host);
        var totals = JsonDocument.Parse(await (await client.PostAsJsonAsync("/api/marks/totals", TotalsBody(), Ct)).Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(0, totals.GetProperty("rowCount").GetInt32());
        Assert.Equal(JsonValueKind.Null, totals.GetProperty("summary").GetProperty("price").ValueKind);
    }

    [Fact]
    public async Task Totals_without_a_marked_column_skip_the_read_and_an_older_as_of_is_refused()
    {
        await using var host = Scoped(api, [P1]);
        var client = await SignInAsync(host);
        var plain = await client.PostAsJsonAsync("/api/marks/totals", TotalsBody(columns: ["deal_name", "market_value"]), Ct);
        Assert.False(plain.Headers.Contains("X-Cache")); // answered before the cache and the database: nothing to overlay
        var none = JsonDocument.Parse(await plain.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal(JsonValueKind.Null, none.GetProperty("rowCount").ValueKind);
        Assert.Empty(none.GetProperty("summary").EnumerateObject());

        var older = await client.PostAsJsonAsync("/api/marks/totals", TotalsBody(asOf: "2026-10-05"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, older.StatusCode);
        var unknown = await client.PostAsJsonAsync("/api/marks/totals", TotalsBody(asOf: "2020-01-01"), Ct);
        Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
    }

    [Fact]
    public void Settings_fall_back_to_their_defaults_when_missing_out_of_range_or_not_numbers()
    {
        static MarksOptions From(params (string, string)[] values) =>
            MarksOptions.From(new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => KeyValuePair.Create(v.Item1, (string?)v.Item2))).Build());

        var defaults = From();
        Assert.Equal(new MarksOptions(TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), 100, 2, 16, TimeSpan.FromSeconds(15)), defaults);
        Assert.Equal(defaults, From(("MARKS_TICK_MS", "10"), ("MARKS_STREAM_SECONDS", "3600"), ("MARKS_MAX_BONDS", "0"),
            ("MARKS_STREAMS_PER_USER", "x"), ("MARKS_STREAMS_GLOBAL", "-1"), ("MARKS_TOTALS_SECONDS", "301")));
        var set = From(("MARKS_TICK_MS", "250"), ("MARKS_STREAM_SECONDS", "0.5"), ("MARKS_MAX_BONDS", "20.7"),
            ("MARKS_STREAMS_PER_USER", "1"), ("MARKS_STREAMS_GLOBAL", "4"), ("MARKS_TOTALS_SECONDS", "30"));
        Assert.Equal(new MarksOptions(TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(500), 20, 1, 4, TimeSpan.FromSeconds(30)), set);
    }
}
