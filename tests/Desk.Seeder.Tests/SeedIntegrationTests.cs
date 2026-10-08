using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Desk.Seeder.Tests;

/// <summary>Runs migrations + the seeder against a throwaway Postgres 17 (Testcontainers), scale 0.1.</summary>
public sealed class SeededDatabase : IAsyncLifetime
{
    // Testcontainers generates a random password per container; nothing is hard-coded.
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24").Build();
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
    public async Task First_run_seeds()
    {
        Assert.Contains("SEED_ACTION=seeded", db.FirstRunOutput);
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot") > 0);
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM core.deal") > 0);
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM app.column_catalog") > 0);
    }

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
    [InlineData("column catalog rows", "SELECT count(*) FROM app.column_catalog", 202)]
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

    [Fact]
    public async Task Column_catalog_matches_the_in_process_catalog()
    {
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand(
            "SELECT name, ordinal, group_name, kind, aggregation, header FROM app.column_catalog ORDER BY ordinal", conn);
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var expected = Desk.Data.Catalog.ColumnCatalog.PositionSnapshot;
        var i = 0;
        while (await r.ReadAsync(TestContext.Current.CancellationToken))
        {
            Assert.True(i < expected.Count, $"extra catalog row {r.GetString(0)}");
            var c = expected[i];
            Assert.Equal(c.Name, r.GetString(0));
            Assert.Equal(i, r.GetInt32(1));
            Assert.Equal(c.Group, r.GetString(2));
            Assert.Equal(c.Kind.ToString(), r.GetString(3));
            Assert.Equal(c.Aggregation.ToString(), r.GetString(4));
            Assert.Equal(c.Header, r.GetString(5));
            i++;
        }
        Assert.Equal(expected.Count, i);
    }

    [Fact]
    public async Task Size_report_does_not_write()
    {
        var before = await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata");
        var o = new StringWriter();
        var report = SeededDatabase.Options() with { SizeReportOnly = true, IfChanged = false };
        Assert.Equal(0, await SeedRunner.RunAsync(report, db.ConnectionString, o, new StringWriter(), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("SEED_ACTION=", o.ToString());
        Assert.Contains("DB_SIZE_MB=", o.ToString());
        Assert.Equal(before, await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata"));
    }

    [Fact]
    public async Task Force_replaces_existing_data()
    {
        var beforeMeta = await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata");
        var o = new StringWriter();
        Assert.Equal(0, await SeedRunner.RunAsync(SeededDatabase.Options(force: true), db.ConnectionString, o, new StringWriter(), TestContext.Current.CancellationToken));
        Assert.Contains("SEED_ACTION=seeded", o.ToString());
        Assert.Contains("forced", o.ToString());
        Assert.True(await db.ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot") > 0);
        Assert.Equal(beforeMeta + 1, await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata"));
    }

    [Fact]
    public async Task Size_guard_rolls_back_and_keeps_the_previous_data()
    {
        var beforeSnap = await db.ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot");
        var beforeMeta = await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata");
        var beforeVersion = await db.ScalarAsync<string>("SELECT version FROM app.seed_metadata ORDER BY id DESC LIMIT 1");
        var err = new StringWriter();
        var tight = SeededDatabase.Options(force: true) with { MaxMegabytes = 1 };
        Assert.Equal(2, await SeedRunner.RunAsync(tight, db.ConnectionString, new StringWriter(), err, TestContext.Current.CancellationToken));
        Assert.Contains("Rolled back", err.ToString());
        Assert.Equal(beforeSnap, await db.ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot"));
        Assert.Equal(beforeMeta, await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata"));
        Assert.Equal(beforeVersion, await db.ScalarAsync<string>("SELECT version FROM app.seed_metadata ORDER BY id DESC LIMIT 1"));
    }

    // #109: the reseed peak (current size + new data, old files kept until COMMIT) is checked before TRUNCATE.
    private async Task<(long Snap, long Meta, string Version)> StateAsync() =>
        (await db.ScalarAsync<long>("SELECT count(*) FROM core.position_snapshot"),
         await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata"),
         await db.ScalarAsync<string>("SELECT version FROM app.seed_metadata ORDER BY id DESC LIMIT 1"));

    [Fact]
    public async Task Peak_over_the_cap_is_refused_before_truncate()
    {
        var before = await StateAsync();
        var o = new StringWriter();
        var err = new StringWriter();
        var tight = SeededDatabase.Options(force: true) with { CapMegabytes = 1 };
        Assert.Equal(2, await SeedRunner.RunAsync(tight, db.ConnectionString, o, err, TestContext.Current.CancellationToken));
        Assert.Contains("SEED_PEAK_EST_MB=", o.ToString());
        Assert.DoesNotContain("Generating", o.ToString()); // refused before generating or loading anything
        Assert.Contains("over the 1 MB storage cap", err.ToString());
        Assert.Contains("Refused before TRUNCATE", err.ToString());
        Assert.Contains("--cap-mb", err.ToString());
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task Peak_under_the_cap_reseeds()
    {
        var before = await StateAsync();
        var o = new StringWriter();
        var roomy = SeededDatabase.Options(force: true) with { CapMegabytes = 10_000 };
        Assert.Equal(0, await SeedRunner.RunAsync(roomy, db.ConnectionString, o, new StringWriter(), TestContext.Current.CancellationToken));
        Assert.Contains("SEED_ACTION=seeded", o.ToString());
        Assert.Contains("cap 10000 MB", o.ToString());
        Assert.Equal(before.Meta + 1, await db.ScalarAsync<long>("SELECT count(*) FROM app.seed_metadata"));
    }

    [Fact]
    public async Task Full_scale_reseed_of_a_full_book_is_refused_at_the_default_cap()
    {
        // A scale-1.0 book commits at ~271 MB; a reseed of it peaks at ~534 MB, over the 512 MB default.
        var before = await StateAsync();
        var err = new StringWriter();
        var full = SeededDatabase.Options(force: true) with { Scale = 1.0m };
        Assert.Equal(SeedOptions.DefaultCapMegabytes, full.CapMegabytes);
        Assert.Equal(2, await SeedRunner.RunAsync(full, db.ConnectionString, new StringWriter(), err,
            (_, _) => Task.FromResult(271L * 1024 * 1024), TestContext.Current.CancellationToken));
        Assert.Contains("peak estimate is 542 MB", err.ToString());
        Assert.Contains("over the 512 MB storage cap", err.ToString());
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task Unreadable_size_fails_closed_before_truncate()
    {
        var before = await StateAsync();
        var o = new StringWriter();
        var err = new StringWriter();
        Assert.Equal(2, await SeedRunner.RunAsync(SeededDatabase.Options(force: true), db.ConnectionString, o, err,
            (_, _) => throw new NpgsqlException("size probe failed"), TestContext.Current.CancellationToken));
        Assert.Contains("cannot read the current database size", err.ToString());
        Assert.Contains("Refused before TRUNCATE", err.ToString());
        Assert.DoesNotContain("SEED_ACTION=", o.ToString());
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task Cancelling_the_size_probe_is_not_swallowed()
    {
        var before = await StateAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SeedRunner.RunAsync(SeededDatabase.Options(force: true), db.ConnectionString, new StringWriter(), new StringWriter(),
                (_, _) => throw new OperationCanceledException(), TestContext.Current.CancellationToken));
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task Skip_path_never_reads_the_size_to_refuse()
    {
        var o = new StringWriter();
        var tiny = SeededDatabase.Options() with { CapMegabytes = 1 };
        var probed = false;
        Assert.Equal(0, await SeedRunner.RunAsync(tiny, db.ConnectionString, o, new StringWriter(),
            (_, _) => { probed = true; throw new NpgsqlException("size unknown"); }, TestContext.Current.CancellationToken));
        Assert.Contains("SEED_ACTION=skipped", o.ToString());
        Assert.False(probed);
    }

    [Fact]
    public async Task Skipped_run_over_the_budget_warns_and_exits_0()
    {
        var err = new StringWriter();
        var tiny = SeededDatabase.Options() with { MaxMegabytes = 1 };
        Assert.Equal(0, await SeedRunner.RunAsync(tiny, db.ConnectionString, new StringWriter(), err, TestContext.Current.CancellationToken));
        Assert.Contains("WARN: pg_database_size is", err.ToString());
        Assert.Contains("skipped run still exits 0", err.ToString());
    }

    [Fact]
    public async Task Copy_failure_mid_load_rolls_back()
    {
        var before = await db.ScalarAsync<long>("SELECT count(*) FROM core.fund");
        await using var conn = new NpgsqlConnection(db.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        var failed = false;
        try
        {
            Loader.Copy(conn, "core.fund",
                ["fund_id", "name", "inception_date", "strategy"],
                [NpgsqlTypes.NpgsqlDbType.Integer, NpgsqlTypes.NpgsqlDbType.Text, NpgsqlTypes.NpgsqlDbType.Date, NpgsqlTypes.NpgsqlDbType.Text],
                [new object?[] { 1, "duplicate pk", new DateOnly(2020, 1, 1), "s" }],
                TestContext.Current.CancellationToken);
        }
        catch (PostgresException)
        {
            failed = true;
        }
        Assert.True(failed, "COPY should have failed on a duplicate fund_id");
        await tx.RollbackAsync(TestContext.Current.CancellationToken);
        Assert.Equal(before, await db.ScalarAsync<long>("SELECT count(*) FROM core.fund"));
    }

    [Fact]
    public async Task Month_end_edge_trades_are_business_day_month_ends()
    {
        Assert.Equal(0L, await db.ScalarAsync<long>("""
            SELECT count(*) FROM core.trade
            WHERE (trade_ts AT TIME ZONE 'America/New_York')::time >= '23:59:59'
              AND extract(isodow FROM trade_ts AT TIME ZONE 'America/New_York') IN (6, 7)
            """));
        Assert.True(await db.ScalarAsync<long>("""
            SELECT count(*) FROM core.trade
            WHERE (trade_ts AT TIME ZONE 'America/New_York')::time >= '23:59:59'
            """) >= 1);
    }

    [Fact]
    public async Task Migrating_data_schemas_down_clears_version_metadata_so_the_next_seed_loads()
    {
        await using var isolated = new PostgreSqlBuilder("postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24").Build();
        await isolated.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var cs = isolated.GetConnectionString();
            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                             .UseNpgsql(cs, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options))
            {
                await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);
                Assert.Equal(0, await SeedRunner.RunAsync(SeededDatabase.Options(), cs, new StringWriter(), new StringWriter(), TestContext.Current.CancellationToken));
                var migrator = ctx.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
                await migrator.MigrateAsync("20261007183543_InitialAppSchema", TestContext.Current.CancellationToken);
            }

            await using (var conn = new NpgsqlConnection(cs))
            {
                await conn.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM app.seed_metadata WHERE version = '1.0.0'", conn);
                Assert.Equal(0L, (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
            }

            await using (var ctx = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                             .UseNpgsql(cs, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options))
            {
                await ctx.Database.MigrateAsync(TestContext.Current.CancellationToken);
            }

            var o = new StringWriter();
            Assert.Equal(0, await SeedRunner.RunAsync(SeededDatabase.Options(), cs, o, new StringWriter(), TestContext.Current.CancellationToken));
            Assert.Contains("SEED_ACTION=seeded", o.ToString());
            await using (var conn = new NpgsqlConnection(cs))
            {
                await conn.OpenAsync(TestContext.Current.CancellationToken);
                await using var cmd = new NpgsqlCommand("SELECT count(*) FROM core.position_snapshot", conn);
                Assert.True((long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0);
            }
        }
        finally
        {
            await isolated.DisposeAsync();
        }
    }
}

public sealed class UnmigratedDatabaseTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24").Build();
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
