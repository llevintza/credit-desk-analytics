using System.Diagnostics;
using Desk.Data.App;
using Desk.Data.Catalog;
using Desk.Seeder.Generation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Desk.Seeder;

/// <summary>The seeding workflow (README §5.5, §14.2), callable from the CLI and from integration tests.</summary>
public static class SeedRunner
{
    /// <summary>Measured committed size at scale 1.0 (README §5.4). Used only to print a peak-size estimate before TRUNCATE.</summary>
    public const double MeasuredMegabytesAtScale1 = 271;

    /// <returns>0 ok (seeded or skipped), 1 not migrated, 2 over the size budget.</returns>
    /// <remarks>Cancelling <paramref name="ct"/> aborts the COPY and rolls back the single seeding transaction.</remarks>
    public static async Task<int> RunAsync(SeedOptions options, string connectionString, TextWriter output, TextWriter err, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                           .UseNpgsql(connectionString, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options))
        {
            if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
            {
                err.WriteLine("ERROR: database has pending migrations. Run the migrations bundle first (README §14.2).");
                return 1;
            }
        }

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);

        var skipped = false;
        if (!options.SizeReportOnly)
        {
            string? lastVersion = null; decimal lastScale = 0; int lastSeed = 0; DateTimeOffset lastAt = default;
            await using (var cmd = new NpgsqlCommand(
                "SELECT version, scale, seed, completed_at FROM app.seed_metadata ORDER BY completed_at DESC LIMIT 1", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct)) { lastVersion = r.GetString(0); lastScale = r.GetDecimal(1); lastSeed = r.GetInt32(2); lastAt = r.GetFieldValue<DateTimeOffset>(3); }

            var catalogCount = await ScalarLongAsync(conn, "SELECT count(*) FROM app.column_catalog", ct);
            var snapshotCount = await ScalarLongAsync(conn, "SELECT count(*) FROM core.position_snapshot", ct);
            var metadataMatches = lastVersion == SeedVersion.Current && lastScale == options.Scale && lastSeed == options.Seed;
            var dataPresent = catalogCount == ColumnCatalog.PositionSnapshot.Count && snapshotCount > 0;
            var upToDate = metadataMatches && dataPresent;
            if (upToDate && options.IfChanged)
            {
                skipped = true;
                output.WriteLine($"SEED_ACTION=skipped (version {SeedVersion.Current}, scale {options.Scale}, seed {options.Seed} already loaded at {lastAt:u})");
            }
            else
            {
                var gen = Stopwatch.StartNew();
                var universe = Universe.Generate(options.Seed, (double)options.Scale, options.AsOf);
                var tables = new Tables(options.Seed, (double)options.Scale, options.AsOf, universe);
                output.WriteLine($"Generating as of {options.AsOf:yyyy-MM-dd} (prior business day {tables.PriorBusinessDay:yyyy-MM-dd}): " +
                                  $"{universe.Deals.Count} deals, {universe.Bonds.Count} bonds, {tables.Positions.Count} positions");

                var dbBefore = await DatabaseSizeAsync(conn, ct);
                var seededBefore = await SeededRelationBytesAsync(conn, ct);
                var peakEstMb = (dbBefore + (long)(MeasuredMegabytesAtScale1 * (double)options.Scale * 1024 * 1024)) / 1024 / 1024;
                output.WriteLine($"SEED_PEAK_EST_MB={peakEstMb} (old files stay until COMMIT; later reseeds peak near 2×)");

                // One transaction: a failed or over-budget reseed leaves the previous data in place (README §14.3).
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    await using (var timeouts = new NpgsqlCommand(
                        "SET LOCAL lock_timeout = '15s'; SET LOCAL statement_timeout = '180s';", conn, tx))
                        await timeouts.ExecuteNonQueryAsync(ct);

                    var stats = Loader.LoadAll(conn, universe, tables, ct, tx);
                    foreach (var s in stats) output.WriteLine($"  {s.Table,-28} {s.Rows,10:N0} rows  {s.Elapsed.TotalSeconds,6:F1} s");

                    var seededAfter = await SeededRelationBytesAsync(conn, ct);
                    var projected = dbBefore - seededBefore + seededAfter;
                    if (projected < 0) projected = seededAfter;
                    var projectedMb = projected / 1024 / 1024;
                    if (projectedMb > options.MaxMegabytes)
                    {
                        await tx.RollbackAsync(ct);
                        err.WriteLine($"ERROR: projected database size is {projectedMb} MB, over the {options.MaxMegabytes} MB budget (README §5.4). Rolled back; previous data kept.");
                        return 2;
                    }

                    await using (var ins = new NpgsqlCommand(
                        "INSERT INTO app.seed_metadata (version, seed, scale, completed_at, database_size_bytes) VALUES (@v, @s, @sc, now(), @sz)", conn, tx))
                    {
                        ins.Parameters.AddWithValue("v", SeedVersion.Current);
                        ins.Parameters.AddWithValue("s", options.Seed);
                        ins.Parameters.AddWithValue("sc", options.Scale);
                        ins.Parameters.AddWithValue("sz", projected);
                        await ins.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                }

                try
                {
                    var analyzeList = string.Join(", ", Loader.SeededTables.Select(Loader.QuoteTable));
                    await using var analyze = new NpgsqlCommand($"ANALYZE {analyzeList}", conn) { CommandTimeout = 600 };
                    await analyze.ExecuteNonQueryAsync(ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    err.WriteLine($"WARN: ANALYZE of seeded tables failed: {ex.GetType().Name}: {ex.Message}");
                }

                output.WriteLine($"SEED_ACTION=seeded (version {SeedVersion.Current}, scale {options.Scale}, seed {options.Seed}, as of {options.AsOf:yyyy-MM-dd}{(options.Force ? ", forced" : "")}) in {gen.Elapsed.TotalSeconds:F1} s");
            }
        }

        long bytes = await DatabaseSizeAsync(conn, ct);
        var mb = bytes / 1024 / 1024;
        output.WriteLine($"DB_SIZE_MB={mb}");
        output.WriteLine($"ELAPSED_S={total.Elapsed.TotalSeconds:F1}");
        // After a successful seed the budget was already enforced inside the transaction (M1).
        // A skipped --if-changed run must not fail the deploy on a leftover over-budget database.
        if (mb > options.MaxMegabytes)
        {
            if (options.SizeReportOnly)
            {
                err.WriteLine($"ERROR: database is {mb} MB, over the {options.MaxMegabytes} MB budget (README §5.4).");
                return 2;
            }
            if (!skipped)
                err.WriteLine($"WARN: pg_database_size is {mb} MB (budget {options.MaxMegabytes} MB); committed data was accepted by the pre-commit guard.");
        }
        return 0;
    }

    internal static async Task<long> DatabaseSizeAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        await using var size = new NpgsqlCommand("SELECT pg_database_size(current_database())", conn);
        return (long)(await size.ExecuteScalarAsync(ct))!;
    }

    internal static async Task<long> SeededRelationBytesAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var parts = string.Join(" + ", Loader.SeededTables.Select(t => $"pg_total_relation_size('{t}'::regclass)"));
        await using var cmd = new NpgsqlCommand($"SELECT COALESCE({parts}, 0)", conn);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync(ct), System.Globalization.CultureInfo.InvariantCulture);
    }

    static async Task<long> ScalarLongAsync(NpgsqlConnection conn, string sql, CancellationToken ct)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        var value = await cmd.ExecuteScalarAsync(ct);
        return value is null or DBNull ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
