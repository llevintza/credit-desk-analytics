using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Desk.Seeder.Tests;

/// <summary>Runs migrations + the seeder against a throwaway Postgres 17 (Testcontainers), scale 0.1.</summary>
public sealed class SeededDatabase : IAsyncLifetime
{
    // Testcontainers generates a random password per container; nothing is hard-coded.
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public string ConnectionString => _pg.GetConnectionString();
    public string FirstRunOutput { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        await _pg.StartAsync();
        await using (var db = NewContext()) await db.Database.MigrateAsync();
        var o = new StringWriter();
        var code = await SeedRunner.RunAsync(Options(), ConnectionString, o, new StringWriter(), TestContext.Current.CancellationToken);
        FirstRunOutput = o.ToString();
        if (code != 0) throw new InvalidOperationException($"seed failed ({code}): {FirstRunOutput}");
    }

    public static readonly DateOnly AsOf = new(2026, 10, 6);

    public static SeedOptions Options(bool force = false) =>
        new(Seed: 42, Scale: 0.1m, IfChanged: !force, Force: force, SizeReportOnly: false, MaxMegabytes: 400, AsOf: AsOf);

    public AppDbContext NewContext() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(ConnectionString, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options);

    public async Task<T> ScalarAsync<T>(string sql)
    {
        await using var conn = new NpgsqlConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync())!;
    }

    public async ValueTask DisposeAsync() => await _pg.DisposeAsync();
}

public sealed class SeedIntegrationTests(SeededDatabase db) : IClassFixture<SeededDatabase>
{
    [Fact]
    public void First_run_seeds() => Assert.Contains("SEED_ACTION=seeded", db.FirstRunOutput);

    [Fact]
    public async Task Second_run_with_if_changed_is_a_no_op()
    {
        var o = new StringWriter();
        Assert.Equal(0, await SeedRunner.RunAsync(SeededDatabase.Options(), db.ConnectionString, o, new StringWriter(), TestContext.Current.CancellationToken));
        Assert.Contains("SEED_ACTION=skipped", o.ToString());
    }

    [Theory]
    [InlineData("deals with zero bonds", "SELECT count(*) FROM core.deal d WHERE NOT EXISTS (SELECT 1 FROM core.bond b WHERE b.deal_id = d.deal_id)", 3)]
    [InlineData("deals with exactly one bond", "SELECT count(*) FROM (SELECT deal_id FROM core.bond GROUP BY deal_id HAVING count(*) = 1) x", 1)]
    [InlineData("bonds with NULL coupon", "SELECT count(*) FROM core.bond WHERE coupon_or_margin IS NULL", 1)]
    [InlineData("positions with zero current face", "SELECT count(*) FROM core.position_snapshot WHERE current_face = 0", 1)]
    [InlineData("month-end trades at 23:59:59 New York time", "SELECT count(*) FROM core.trade WHERE (trade_ts AT TIME ZONE 'America/New_York')::time >= '23:59:59'", 1)]
    [InlineData("fund flows sharing a date", "SELECT count(*) FROM (SELECT fund_id, flow_date FROM core.fund_flow GROUP BY 1, 2 HAVING count(*) > 1) x", 1)]
    [InlineData("column catalog rows", "SELECT count(*) FROM app.column_catalog", 200)]
    [InlineData("snapshot as-of dates", "SELECT count(DISTINCT as_of_date) FROM core.position_snapshot", 2)]
    public async Task Spec_edge_cases_are_present(string what, string sql, long atLeast)
    {
        var n = await db.ScalarAsync<long>(sql);
        Assert.True(n >= atLeast, $"{what}: {n} < {atLeast}");
    }

    [Fact]
    public async Task Mid_month_inception_fund_starts_at_its_first_month_end() =>
        Assert.Equal(new DateOnly(2023, 3, 31), await db.ScalarAsync<DateOnly>("SELECT min(as_of_month) FROM core.fund_performance WHERE fund_id = 4"));

    [Fact]
    public async Task Any_one_bond_per_deal_keeps_every_deal()
    {
        var deals = await db.ScalarAsync<long>("SELECT count(*) FROM core.deal");
        var rows = await db.ScalarAsync<long>("""
            SELECT count(*) FROM core.deal d
            LEFT JOIN LATERAL (SELECT b.class FROM core.bond b WHERE b.deal_id = d.deal_id
                               ORDER BY b.seniority_rank, b.bond_id LIMIT 1) b ON true
            """);
        Assert.Equal(deals, rows);
    }

    [Fact]
    public async Task Summing_a_parent_field_after_joining_children_double_counts()
    {
        // The aggregation-grain trap (README §6 P3/P4): the naive join multiplies deal balances by bond count.
        var truth = await db.ScalarAsync<decimal>("SELECT sum(original_balance) FROM core.deal");
        var naive = await db.ScalarAsync<decimal>("SELECT sum(d.original_balance) FROM core.deal d JOIN core.bond b ON b.deal_id = d.deal_id");
        var grainSafe = await db.ScalarAsync<decimal>("""
            SELECT sum(d.original_balance) FROM core.deal d
            LEFT JOIN (SELECT deal_id, sum(current_balance) AS cur FROM core.bond GROUP BY deal_id) bb ON bb.deal_id = d.deal_id
            """);
        Assert.True(naive > truth);
        Assert.Equal(truth, grainSafe);
    }

    [Fact]
    public async Task No_trade_is_after_the_as_of_date_or_on_a_weekend()
    {
        // Month-end 23:59:59 trades must sit on a business day on or before as-of.
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM core.trade WHERE (trade_ts AT TIME ZONE 'America/New_York')::date > DATE '2026-10-06'"));
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM core.trade WHERE extract(isodow FROM trade_ts AT TIME ZONE 'America/New_York') IN (6, 7)"));
    }

    [Fact]
    public async Task Cost_basis_is_the_same_on_both_as_of_dates() =>
        Assert.Equal(0L, await db.ScalarAsync<long>(
            "SELECT count(*) FROM core.position_snapshot a " +
            "JOIN core.position_snapshot b ON b.position_id = a.position_id AND b.as_of_date < a.as_of_date " +
            "WHERE a.book_price IS DISTINCT FROM b.book_price"));

    [Fact]
    public async Task Cancelling_mid_seed_rolls_back_to_the_previous_data()
    {
        var before = await db.ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot");
        var runs = await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata");
        using var cts = new CancellationTokenSource();
        // Cancel when generation is announced: the TRUNCATE then runs and the first COPY sees the token.
        var output = new CancelOnWrite("Generating", cts);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SeedRunner.RunAsync(SeededDatabase.Options(force: true), db.ConnectionString, output, new StringWriter(), cts.Token));
        Assert.Equal(before, await db.ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot"));
        Assert.Equal(runs, await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata"));
    }

    private sealed class CancelOnWrite(string marker, CancellationTokenSource cts) : StringWriter
    {
        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            if (value?.Contains(marker, StringComparison.Ordinal) == true) cts.Cancel();
        }
    }

    [Fact]
    public async Task Database_stays_inside_the_size_budget() =>
        Assert.True(await db.ScalarAsync<long>("SELECT pg_database_size(current_database())") < 400L * 1024 * 1024);
}

public sealed class UnmigratedDatabaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine").Build();
    public async ValueTask InitializeAsync() => await _pg.StartAsync();
    public async ValueTask DisposeAsync() => await _pg.DisposeAsync();

    [Fact]
    public async Task Seeder_refuses_to_run_before_migrations()
    {
        var err = new StringWriter();
        Assert.Equal(1, await SeedRunner.RunAsync(SeededDatabase.Options(), _pg.GetConnectionString(), new StringWriter(), err, TestContext.Current.CancellationToken));
        Assert.Contains("pending migrations", err.ToString());
    }
}
