using Desk.Data.App;
using Desk.Data.App.Migrations;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Desk.Data.Tests;

/// <summary>A throwaway Postgres 17 (Testcontainers) with every migration applied and no seed data.</summary>
public sealed class MigratedDatabase : IAsyncLifetime
{
    // Testcontainers generates a random password per container; nothing is hard-coded.
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder("postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24").Build();

    public async ValueTask InitializeAsync()
    {
        await _pg.StartAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(_pg.GetConnectionString(), n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options);
        await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    public async Task<NpgsqlConnection> OpenAsync()
    {
        var conn = new NpgsqlConnection(_pg.GetConnectionString());
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        return conn;
    }

    public async ValueTask DisposeAsync() => await _pg.DisposeAsync();
}

/// <summary>
/// #192, README §8 "never NaN": the summary's weight and measures reject NaN at write time. #304: the float8 measures
/// also reject ±Infinity (their #286 _not_nan checks became _finite checks); numeric(18,2) rejects Infinity by its typmod.
/// </summary>
public sealed class NanCheckConstraintTests(MigratedDatabase db) : IClassFixture<MigratedDatabase>
{
    private static readonly DateOnly AsOf = new(2026, 10, 6);
    private static long _nextPosition = 1000;

    public static TheoryData<string> GuardedColumns() => [.. SnapshotNanChecks.NumericMeasures.Concat(SnapshotNanChecks.Float8Measures)];

    public static TheoryData<string, string> InfiniteFloat8Values() =>
        [.. SnapshotFiniteChecks.Columns.SelectMany(c => new[] { (c, "'Infinity'"), (c, "'-Infinity'") })];

    // The constraint that guards a column today: numeric keeps #286's _not_nan, float8 has #304's _finite.
    private static string GuardName(string column) => SnapshotFiniteChecks.Columns.Contains(column)
        ? SnapshotFiniteChecks.ConstraintName(column)
        : SnapshotNanChecks.ConstraintName(column);

    [Theory]
    [MemberData(nameof(GuardedColumns))]
    public async Task A_NaN_in_a_guarded_column_is_rejected(string column)
    {
        await using var conn = await db.OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(conn, column, "'NaN'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(GuardName(column), ex.ConstraintName);
    }

    [Theory]
    [MemberData(nameof(InfiniteFloat8Values))]
    public async Task An_infinity_in_a_float8_measure_is_rejected(string column, string literal)
    {
        await using var conn = await db.OpenAsync();
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(conn, column, literal));
        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
        Assert.Equal(SnapshotFiniteChecks.ConstraintName(column), ex.ConstraintName);
    }

    [Theory]
    [InlineData("NULL")]
    [InlineData("1.7976931348623157e308")]
    [InlineData("-1.7976931348623157e308")]
    [InlineData("0")]
    public async Task Null_and_the_largest_finite_values_pass_every_float8_check(string literal)
    {
        // Every float8 measure set at once, so each of the finite checks sees the value.
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO core.position_snapshot (as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, sector, " +
            $"market_value, {string.Join(", ", SnapshotFiniteChecks.Columns)}) VALUES (@d, @id, 1, 1, 1, 1, 'TEST00001', 'Test deal', 'RMBS', 1, " +
            $"{string.Join(", ", SnapshotFiniteChecks.Columns.Select(_ => literal + "::float8"))})", conn);
        cmd.Parameters.AddWithValue("d", AsOf);
        cmd.Parameters.AddWithValue("id", Interlocked.Increment(ref _nextPosition));
        Assert.Equal(1, await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Every_constraint_exists_and_is_validated()
    {
        await using var conn = await db.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT conname FROM pg_constraint WHERE conrelid = 'core.position_snapshot'::regclass AND contype = 'c' AND convalidated", conn);
        var names = new List<string>();
        await using (var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken))
            while (await r.ReadAsync(TestContext.Current.CancellationToken))
                names.Add(r.GetString(0));

        Assert.Equal(GuardedColumns().Select(c => GuardName(c.Data)).Order(), names.Order());
    }

    [Fact]
    public async Task Null_and_finite_values_still_insert_and_the_summary_is_unchanged()
    {
        // A NULL measure is "missing", not NaN: the CHECK passes, and the row doesn't count toward that column's weight.
        await using var conn = await db.OpenAsync();
        var day = AsOf.AddDays(-1);
        await InsertAsync(conn, "spread_bp", "10", marketValue: 100m, asOf: day);
        await InsertAsync(conn, "spread_bp", "20", marketValue: -300m, asOf: day); // a short: weighs |MV|
        await InsertAsync(conn, "spread_bp", "NULL", marketValue: 50m, asOf: day);

        var spread = ColumnCatalog.PositionSnapshot.Single(c => c.Name == "spread_bp");
        var mv = ColumnCatalog.PositionSnapshot.Single(c => c.Name == GridSqlBuilder.WeightColumn);
        await using var cmd = new NpgsqlCommand(
            $"SELECT {GridSqlBuilder.Aggregate(spread)}, {GridSqlBuilder.Aggregate(mv)} FROM {GridSqlBuilder.SummaryFrom} WHERE as_of_date = @d", conn);
        cmd.Parameters.AddWithValue("d", day);
        await using var r = await cmd.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        Assert.True(await r.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(17.5, r.GetDouble(0), 9); // (10·100 + 20·300) / 400
        Assert.Equal(-150m, r.GetDecimal(1));
    }

    [Fact]
    public async Task Up_and_Down_rerun_cleanly()
    {
        // A retried deploy, or a constraint made by hand: each step can run twice. DDL is transactional, so roll back.
        var migration = new SnapshotNanChecks();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>()).Sql;

        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        foreach (var sql in new[] { up, down, down, up })
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        var ex = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(conn, "market_value", "'NaN'", tx: tx));
        Assert.Equal(SnapshotNanChecks.ConstraintName("market_value"), ex.ConstraintName);
    }

    [Fact]
    public async Task The_finite_checks_rerun_cleanly_and_Down_restores_the_NaN_checks()
    {
        var migration = new SnapshotFiniteChecks();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>()).Sql;

        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(conn, tx, up, down, down);
        await tx.SaveAsync("down", TestContext.Current.CancellationToken);
        // After Down: #286's state, so NaN is rejected again but an Infinity gets in.
        var nan = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(conn, "spread_bp", "'NaN'", tx: tx));
        Assert.Equal(SnapshotNanChecks.ConstraintName("spread_bp"), nan.ConstraintName);
        await tx.RollbackAsync("down", TestContext.Current.CancellationToken);
        await InsertAsync(conn, "spread_bp", "'Infinity'", tx: tx);
        await tx.RollbackAsync("down", TestContext.Current.CancellationToken);

        await ExecuteAsync(conn, tx, up, up);
        var inf = await Assert.ThrowsAsync<PostgresException>(() => InsertAsync(conn, "spread_bp", "'-Infinity'", tx: tx));
        Assert.Equal(SnapshotFiniteChecks.ConstraintName("spread_bp"), inf.ConstraintName);
    }

    private static async Task ExecuteAsync(NpgsqlConnection conn, NpgsqlTransaction tx, params string[] statements)
    {
        foreach (var sql in statements)
        {
            await using var cmd = new NpgsqlCommand(sql, conn, tx);
            await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static async Task InsertAsync(NpgsqlConnection conn, string column, string literal, decimal? marketValue = null,
        DateOnly? asOf = null, NpgsqlTransaction? tx = null)
    {
        // Only the NOT NULL columns, the weight and the column under test (the snapshot has no foreign keys).
        var extra = column == "market_value" ? "" : ", market_value";
        var extraValue = column == "market_value" ? "" : ", @mv";
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO core.position_snapshot (as_of_date, position_id, portfolio_id, fund_id, bond_id, deal_id, cusip, deal_name, sector, " +
            $"{column}{extra}) VALUES (@d, @id, 1, 1, 1, 1, 'TEST00001', 'Test deal', 'RMBS', {literal}{extraValue})", conn, tx);
        cmd.Parameters.AddWithValue("d", asOf ?? AsOf);
        cmd.Parameters.AddWithValue("id", Interlocked.Increment(ref _nextPosition));
        cmd.Parameters.AddWithValue("mv", marketValue ?? 1m);
        await cmd.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
