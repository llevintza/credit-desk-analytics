using System.Data;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.Limits;
using Desk.Data;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Desk.Data.Sources;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// #241: a first view reads its page and its totals on two pooled connections at once, but only with a spare shared
/// database permit for the second one (<see cref="DatabasePermits.TryTakeSpare"/>): every connection a request holds
/// is still a permit, so the permits keep fitting the pool (<see cref="ConnectionBudget"/>).
/// </summary>
public sealed class DatabasePermitsTests
{
    private static DatabasePermits Permits(int concurrency) =>
        new(LimitsOptions.From(new ConfigurationBuilder().Build()) with { GlobalConcurrency = concurrency });

    [Fact]
    public void A_spare_is_taken_only_while_another_permit_stays_free()
    {
        using var permits = Permits(3);
        using var request = permits.Shared.AttemptAcquire(1); // the rate limiter's permit for the request itself
        Assert.True(request.IsAcquired);

        using var spare = permits.TryTakeSpare();
        Assert.True(spare.IsAcquired);
        Assert.Equal(1, permits.Shared.GetStatistics()!.CurrentAvailablePermits); // one shared count, not two limiters

        // The last free permit is left for the next request.
        Assert.False(permits.TryTakeSpare().IsAcquired);
        spare.Dispose();
        using var again = permits.TryTakeSpare();
        Assert.True(again.IsAcquired);
    }

    [Fact]
    public void With_a_single_permit_there_is_never_a_spare()
    {
        using var permits = Permits(1);
        Assert.False(permits.TryTakeSpare().IsAcquired);
        Assert.Equal(1, permits.Shared.GetStatistics()!.CurrentAvailablePermits);
    }

    [Fact]
    public async Task A_spare_is_never_taken_while_a_request_waits_for_a_permit()
    {
        using var permits = Permits(3);
        var held = Enumerable.Range(0, 3).Select(_ => permits.Shared.AttemptAcquire(1)).ToList();
        var waiting = permits.Shared.AcquireAsync(1, TestContext.Current.CancellationToken);
        Assert.False(waiting.IsCompleted);

        Assert.False(permits.TryTakeSpare().IsAcquired);
        held[0].Dispose(); // the freed permit goes to the queued request, not to a spare
        using var served = await waiting;
        Assert.True(served.IsAcquired);
        Assert.False(permits.TryTakeSpare().IsAcquired);
        held.ForEach(l => l.Dispose());
    }

    [Fact]
    public void The_shared_limiter_is_never_idle_so_the_rate_limiter_keeps_the_same_instance()
    {
        using var permits = Permits(8);
        Assert.Null(permits.Shared.IdleDuration);
    }
}

/// <summary>#241: <see cref="GridRepository.ReadBlockAsync"/> with <c>concurrentSummary</c>, and the endpoint that chooses it.</summary>
[Collection(ApiCollection.Name)]
public sealed class ConcurrentSummaryTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly IReadOnlyCollection<ColumnDef> Catalog = ColumnCatalog.PositionSnapshot;
    private static readonly ColumnDef PositionId = Catalog.Single(c => c.Name == GridQueryNormalizer.RowIdColumn);

    private static GridQuery AllPreset(params int[] portfolios) => new GridQueryNormalizer(Catalog).Normalize(
        new GridRequest(Columns: [.. BuiltInPresets.All], SortModel: [new("market_value", "desc")]), PostgresApiFactory.AsOf, portfolios);

    private (GridRepository Grid, CountingSources Sources) Repository()
    {
        var sources = new CountingSources(api.Services.GetRequiredService<IDataSourceRegistry>());
        return (new GridRepository(sources), sources);
    }

    [Fact]
    public async Task A_concurrent_first_view_reads_the_same_block_and_totals_as_one_batch_on_two_connections_at_once()
    {
        var (grid, sources) = Repository();
        var query = AllPreset(1, 2);

        var batch = await grid.ReadBlockAsync(query, Catalog, Ct);
        Assert.Equal(1, sources.Peak);
        sources.Reset();
        // The totals can finish before the page's connection opens, so a peak of 2 is held for, not left to timing.
        sources.HoldUntilOpenTogether(2);
        var concurrent = await grid.ReadBlockAsync(query, Catalog, Ct, concurrentSummary: true);

        Assert.Equal(2, sources.Peak);
        Assert.Equal(0, sources.Open);
        Assert.True(batch.RowCount > 0);
        Assert.Equal(batch.RowCount, concurrent.RowCount);
        Assert.Equal(batch.Summary, concurrent.Summary);
        Assert.Equal(batch.Rows, concurrent.Rows);
        Assert.Equal(batch.Columns.Select(Values), concurrent.Columns.Select(Values));
    }

    [Fact]
    public async Task The_hold_fails_a_read_that_never_has_two_connections_open_at_once()
    {
        // What makes the peak of 2 above a proof: one batch on one connection never reaches the hold's count.
        var (grid, sources) = Repository();
        sources.HoldUntilOpenTogether(2, TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<TimeoutException>(() => grid.ReadBlockAsync(AllPreset(1, 2), Catalog, Ct));
        Assert.Equal(1, sources.Peak);
        Assert.Equal(0, sources.Open);
    }

    [Fact]
    public async Task Concurrent_totals_keep_the_portfolio_scope_in_sql()
    {
        var (grid, _) = Repository();
        var one = await grid.ReadBlockAsync(AllPreset(1), Catalog, Ct, concurrentSummary: true);
        var none = await grid.ReadBlockAsync(AllPreset(), Catalog, Ct, concurrentSummary: true);

        await using var conn = new NpgsqlConnection(api.ConnectionString);
        await conn.OpenAsync(Ct);
        await using var cmd = new NpgsqlCommand("SELECT count(*)::int FROM core.position_snapshot WHERE as_of_date = @d AND portfolio_id = 1", conn);
        cmd.Parameters.AddWithValue("d", PostgresApiFactory.AsOf);
        Assert.Equal((int)(await cmd.ExecuteScalarAsync(Ct))!, one.RowCount);
        Assert.Equal(0, none.RowCount);
        Assert.Equal(0, none.Rows);
    }

    [Fact]
    public async Task Known_totals_read_only_the_page_whatever_the_flag()
    {
        var (grid, sources) = Repository();
        var known = new GridSummary(7, []);
        var block = await grid.ReadBlockAsync(AllPreset(1), Catalog, Ct, known, concurrentSummary: true);
        Assert.Equal(1, sources.Peak);
        Assert.Equal(7, block.RowCount);
    }

    [Fact]
    public async Task A_failing_page_read_cancels_the_totals_and_is_what_the_caller_sees()
    {
        var (grid, sources) = Repository();
        // Sorted by a column the table lacks: only the page statement fails (42703); the totals don't sort.
        var missing = new ColumnDef("no_such_column", "Test", ColumnKind.Ratio, Aggregation.None, "Missing");
        var query = AllPreset(1) with { Sort = [new GridSort(missing, false), new GridSort(PositionId, false)] };

        var e = await Assert.ThrowsAsync<PostgresException>(() => grid.ReadBlockAsync(query, Catalog, Ct, concurrentSummary: true));
        Assert.Equal(PostgresErrorCodes.UndefinedColumn, e.SqlState);
        Assert.Equal(0, sources.Open);
    }

    [Fact]
    public async Task A_failing_summary_is_what_the_caller_sees_not_the_cancelled_page()
    {
        var (grid, sources) = Repository();
        // SUM over a text column: the page reads it fine, the totals statement fails (42883).
        var summedText = new ColumnDef("deal_name", "Test", ColumnKind.Text, Aggregation.Sum, "Deal");
        var query = AllPreset(1) with { Columns = [PositionId, summedText] };

        var e = await Assert.ThrowsAsync<PostgresException>(() => grid.ReadBlockAsync(query, Catalog, Ct, concurrentSummary: true));
        Assert.Equal(PostgresErrorCodes.UndefinedFunction, e.SqlState);
        Assert.Equal(0, sources.Open);
    }

    [Fact]
    public async Task A_cancelled_caller_cancels_both_reads()
    {
        var (grid, sources) = Repository();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => grid.ReadBlockAsync(AllPreset(1), Catalog, cancelled.Token, concurrentSummary: true));
        Assert.Equal(0, sources.Open);
    }

    [Fact]
    public async Task The_endpoint_reads_a_first_view_on_two_connections_and_a_later_block_on_one()
    {
        var sources = new Holder();
        await using var host = WithCountingSources(sources);
        var client = await SignedInAsync(host);
        await PostAsync(client, Filtered(-1e15 - 3, startRow: 0)); // loads the meta cache, its own core read

        sources.Value!.Reset();
        sources.Value.HoldUntilOpenTogether(2);
        var first = await PostAsync(client, Filtered(-1e15 - 1, startRow: 0));
        Assert.Equal(2, sources.Value.Peak);
        sources.Value.Release();
        sources.Value.Reset();
        var later = await PostAsync(client, Filtered(-1e15 - 1, startRow: 100));
        Assert.Equal(1, sources.Value.Peak);
        Assert.Equal(first.GetProperty("rowCount").GetInt32(), later.GetProperty("rowCount").GetInt32());
        Assert.Equal(first.GetProperty("summary").GetRawText(), later.GetProperty("summary").GetRawText());
    }

    [Fact]
    public async Task Without_a_spare_permit_the_first_view_is_one_batch_on_one_connection()
    {
        var sources = new Holder();
        await using var host = WithCountingSources(sources, ("RATE_LIMIT_GLOBAL_CONCURRENCY", "1"));
        var client = await SignedInAsync(host);

        var first = await PostAsync(client, Filtered(-1e15 - 2, startRow: 0));
        Assert.Equal(1, sources.Value!.Peak);
        Assert.True(first.GetProperty("rowCount").GetInt32() > 0);
    }

    [Fact]
    public async Task Many_first_views_at_once_all_finish_and_never_hold_more_connections_than_permits()
    {
        // Three permits for six users who all open a brand-new view at the same moment: no request waits on a second
        // permit while holding its first (no deadlock), and connections never outnumber the permits.
        var sources = new Holder();
        await using var host = WithCountingSources(sources, ("RATE_LIMIT_GLOBAL_CONCURRENCY", "3"), ("RATE_LIMIT_GLOBAL_QUEUE", "32"));
        var clients = new List<HttpClient>();
        for (var i = 0; i < 6; i++)
            clients.Add(await SignedInAsync(host));
        sources.Value!.Reset();

        var responses = await Task.WhenAll(clients.Select((c, i) => PostAsync(c, Filtered(-1e15 - 10 - i, startRow: 0))))
            .WaitAsync(TimeSpan.FromSeconds(60), Ct);

        Assert.All(responses, r => Assert.True(r.GetProperty("rowCount").GetInt32() > 0));
        Assert.InRange(sources.Value.Peak, 1, 3);
    }

    private static object Filtered(double threshold, int startRow) => new
    {
        startRow,
        endRow = startRow + 100,
        columns = BuiltInPresets.All,
        sortModel = new[] { new { colId = "market_value", sort = "desc" } },
        filterModel = new Dictionary<string, object> { ["spread_bp"] = new { filterType = "number", type = "greaterThan", filter = threshold } },
    };

    private static async Task<JsonElement> PostAsync(HttpClient client, object body)
    {
        var res = await client.PostAsJsonAsync("/api/positions/query", body, Ct);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct)).RootElement.Clone();
    }

    private async Task<HttpClient> SignedInAsync(WebApplicationFactory<Program> host)
    {
        var client = PostgresApiFactory.NewClient(host);
        await PostgresApiFactory.LoginAsync(client, (await api.CreateUserAsync()).Email!);
        return client;
    }

    private WebApplicationFactory<Program> WithCountingSources(Holder holder, params (string Key, string Value)[] settings) =>
        api.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IDataSourceRegistry>();
                s.AddSingleton<IDataSourceRegistry>(sp => holder.Value = new CountingSources(
                    new DataSourceRegistry(sp.GetRequiredService<IConfiguration>(), sp.GetRequiredService<DbConnectionCounter>())));
            });
        });

    private static IEnumerable<object?> Values(GridColumn column) => column switch
    {
        Int64Column c => c.Values.Cast<object?>(),
        Int32Column c => c.Values.Cast<object?>(),
        DoubleColumn c => c.Values.Cast<object?>(),
        DecimalColumn c => c.Values.Cast<object?>(),
        TextColumn c => c.Values.Cast<object?>(),
        DateColumn c => c.Values.Cast<object?>(),
        BoolColumn c => c.Values.Cast<object?>(),
        _ => throw new InvalidOperationException(column.GetType().Name),
    };

    private sealed class Holder
    {
        public CountingSources? Value { get; set; }
    }

    /// <summary>
    /// Counts the <c>core</c> connections open at once (Dapper's reads); everything else passes through. While a
    /// <see cref="HoldUntilOpenTogether">hold</see> is set, each new connection waits until that many are open, so
    /// reads that run concurrently are seen open together whatever their timing, and reads that don't time out.
    /// </summary>
    private sealed class CountingSources(IDataSourceRegistry inner) : IDataSourceRegistry
    {
        private int _open;
        private int _peak;
        private Hold? _hold;

        public int Open => Volatile.Read(ref _open);

        public int Peak => Volatile.Read(ref _peak);

        public void Reset() => Volatile.Write(ref _peak, Open);

        public void HoldUntilOpenTogether(int count, TimeSpan? timeout = null) =>
            Volatile.Write(ref _hold, new Hold(count, timeout ?? TimeSpan.FromSeconds(30)));

        public void Release() => Volatile.Write(ref _hold, null);

        public NpgsqlDataSource Get(string source) => inner.Get(source);

        public async ValueTask<NpgsqlConnection> OpenAsync(string source, CancellationToken ct)
        {
            var conn = await inner.OpenAsync(source, ct);
            if (source != ConnectionStrings.Core)
                return conn;
            var now = Interlocked.Increment(ref _open);
            for (var peak = Peak; now > peak && Interlocked.CompareExchange(ref _peak, now, peak) != peak; peak = Peak) { }
            conn.StateChange += (_, e) =>
            {
                if (e.CurrentState == ConnectionState.Closed) Interlocked.Decrement(ref _open);
            };
            if (Volatile.Read(ref _hold) is { } hold)
            {
                try
                {
                    await hold.ArriveAsync(now, ct);
                }
                catch
                {
                    await conn.DisposeAsync();
                    throw;
                }
            }
            return conn;
        }

        private sealed class Hold(int count, TimeSpan timeout)
        {
            private readonly TaskCompletionSource _together = new(TaskCreationOptions.RunContinuationsAsynchronously);

            public Task ArriveAsync(int open, CancellationToken ct)
            {
                if (open >= count) _together.TrySetResult();
                return _together.Task.WaitAsync(timeout, ct);
            }
        }
    }
}
