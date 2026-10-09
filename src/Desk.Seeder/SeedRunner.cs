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
    /// <summary>Measured committed size at scale 1.0 (README §5.4). The new-data half of the reseed peak estimate.</summary>
    public const double MeasuredMegabytesAtScale1 = 271;

    /// <summary>Exit code for a cancellation, before COMMIT (thrown to Program.cs), after it (returned here, #285), or during
    /// the final size read of a skip or <c>--size-report</c> (returned here, #324).</summary>
    public const int CancelledExitCode = 130;

    /// <returns>0 ok (seeded or skipped), 1 not migrated, 2 over the size budget or the peak cap, 130 cancelled after COMMIT
    /// or during the final size read of a skip or <c>--size-report</c>.</returns>
    /// <remarks>Cancelling <paramref name="ct"/> before COMMIT aborts the COPY, rolls back the single seeding transaction and
    /// throws. A cancellation after COMMIT (during ANALYZE or the size report) keeps the data, says so and returns 130 (#285).</remarks>
    public static Task<int> RunAsync(SeedOptions options, string connectionString, TextWriter output, TextWriter err, CancellationToken ct = default) =>
        RunAsync(options, connectionString, output, err, DatabaseSizeAsync, ct);

    /// <param name="databaseSize">Reads the current database size in bytes for the pre-TRUNCATE peak check. Injectable so
    /// tests can simulate an unreadable size; any failure other than cancellation refuses the reseed (fail closed).</param>
    public static Task<int> RunAsync(SeedOptions options, string connectionString, TextWriter output, TextWriter err,
        Func<NpgsqlConnection, CancellationToken, Task<long>> databaseSize, CancellationToken ct) =>
        RunAsync(options, connectionString, output, err, databaseSize, AnalyzeAsync, ct);

    /// <param name="analyze">Runs ANALYZE on the seeded tables after COMMIT. Injectable so tests can cancel after COMMIT (#285).</param>
    public static async Task<int> RunAsync(SeedOptions options, string connectionString, TextWriter output, TextWriter err,
        Func<NpgsqlConnection, CancellationToken, Task<long>> databaseSize, Func<NpgsqlConnection, CancellationToken, Task> analyze,
        CancellationToken ct)
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
        var committed = false;
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
                // Pre-flight peak check (#109), truncate paths only: the skip path above never reads the size to refuse
                // (its final DB_SIZE_MB report below is best-effort and can't fail it).
                // TRUNCATE keeps the old files until COMMIT, so the reseed peaks near current size + new data.
                long dbBefore;
                try { dbBefore = await databaseSize(conn, ct); }
                // A cancellation can surface as a non-OCE (e.g. NpgsqlException); let it propagate so it exits 130, not 2.
                catch (Exception ex) when (ex is not OperationCanceledException && !ct.IsCancellationRequested)
                {
                    err.WriteLine($"ERROR: cannot read the current database size ({ex.GetType().Name}: {ex.Message}), so the reseed peak " +
                                  $"cannot be checked against the {options.CapMegabytes} MB cap. Refused before TRUNCATE; nothing was changed.");
                    return 2;
                }
                var peakEstMb = PeakEstimateMegabytes(dbBefore, options.Scale);
                output.WriteLine($"SEED_PEAK_EST_MB={peakEstMb} (current {dbBefore / 1024 / 1024} MB + new data ~{NewDataMegabytes(options.Scale)} MB; old files stay until COMMIT; cap {options.CapMegabytes} MB)");
                if (peakEstMb > options.CapMegabytes)
                {
                    err.WriteLine($"ERROR: reseed peak estimate is {peakEstMb} MB (current database {dbBefore / 1024 / 1024} MB + new data ~{NewDataMegabytes(options.Scale)} MB), " +
                                  $"over the {options.CapMegabytes} MB storage cap. Refused before TRUNCATE; data and app.seed_metadata are unchanged. " +
                                  "TRUNCATE keeps the old files until COMMIT, so a reseed needs about old + new. If the database's real storage cap is higher, " +
                                  "pass a larger --cap-mb (README §10)." +
                                  (options.CapMegabytes == SeedOptions.DefaultCapMegabytes
                                      ? $" At the {SeedOptions.DefaultCapMegabytes} MB default a full-book scale-1.0 reseed is refused on purpose until the Neon cap is confirmed."
                                      : ""));
                    return 2;
                }

                var gen = Stopwatch.StartNew();
                var universe = Universe.Generate(options.Seed, (double)options.Scale, options.AsOf);
                var tables = new Tables(options.Seed, (double)options.Scale, options.AsOf, universe);
                output.WriteLine($"Generating as of {options.AsOf:yyyy-MM-dd} (prior business day {tables.PriorBusinessDay:yyyy-MM-dd}): " +
                                  $"{universe.Deals.Count} deals, {universe.Bonds.Count} bonds, {tables.Positions.Count} positions");

                // dbBefore (read before generating) feeds the peak guard; the pre-commit projection needs a size read
                // next to seededBefore, so both describe the same moment.
                var dbAtLoad = await databaseSize(conn, ct);
                var seededBefore = await SeededRelationBytesAsync(conn, ct);

                // One transaction: a failed or over-budget reseed leaves the previous data in place (README §14.3).
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    await using (var timeouts = new NpgsqlCommand(
                        "SET LOCAL lock_timeout = '15s'; SET LOCAL statement_timeout = '180s';", conn, tx))
                        await timeouts.ExecuteNonQueryAsync(ct);

                    var stats = Loader.LoadAll(conn, universe, tables, ct, tx);
                    foreach (var s in stats) output.WriteLine($"  {s.Table,-28} {s.Rows,10:N0} rows  {s.Elapsed.TotalSeconds,6:F1} s");

                    var seededAfter = await SeededRelationBytesAsync(conn, ct);
                    var projected = dbAtLoad - seededBefore + seededAfter;
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
                    committed = true;
                }

                try { await analyze(conn, ct); }
                // The data is committed: a cancellation (OCE or e.g. an NpgsqlException) must not be reported as a rollback.
                catch (Exception) when (ct.IsCancellationRequested) { return CancelledAfterCommit(options, err); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    err.WriteLine($"WARN: ANALYZE of seeded tables failed: {ex.GetType().Name}: {ex.Message}");
                }

                output.WriteLine($"SEED_ACTION=seeded (version {SeedVersion.Current}, scale {options.Scale}, seed {options.Seed}, as of {options.AsOf:yyyy-MM-dd}{(options.Force ? ", forced" : "")}) in {gen.Elapsed.TotalSeconds:F1} s");
            }
        }

        // Best-effort report read: an unreadable size must not fail a skipped (or already committed) run (#109 req. 2).
        // Only --size-report, whose whole job is this number, fails on it.
        long bytes;
        try { bytes = await databaseSize(conn, ct); }
        catch (Exception) when (committed && ct.IsCancellationRequested) { return CancelledAfterCommit(options, err); }
        // Only a skip or --size-report gets here uncommitted: nothing was written, so this is no rollback (#324).
        catch (Exception) when (ct.IsCancellationRequested)
        {
            err.WriteLine("ERROR: cancelled; nothing was changed (seed skipped / size report only).");
            return CancelledExitCode;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            output.WriteLine("DB_SIZE_MB=unknown");
            output.WriteLine($"ELAPSED_S={total.Elapsed.TotalSeconds:F1}");
            err.WriteLine($"WARN: cannot read pg_database_size ({ex.GetType().Name}: {ex.Message}).");
            return options.SizeReportOnly ? 2 : 0;
        }
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
            err.WriteLine(skipped
                ? $"WARN: pg_database_size is {mb} MB, over the {options.MaxMegabytes} MB budget; the seed is unchanged, so this skipped run still exits 0."
                : $"WARN: pg_database_size is {mb} MB (budget {options.MaxMegabytes} MB); committed data was accepted by the pre-commit guard.");
        }
        return 0;
    }

    static int CancelledAfterCommit(SeedOptions options, TextWriter err)
    {
        err.WriteLine($"ERROR: cancelled after the seed was committed (version {SeedVersion.Current}, scale {options.Scale}); ANALYZE/size report skipped.");
        return CancelledExitCode;
    }

    /// <summary>ANALYZE of every seeded table, run after COMMIT so the planner sees the new data.</summary>
    public static async Task AnalyzeAsync(NpgsqlConnection conn, CancellationToken ct)
    {
        var analyzeList = string.Join(", ", Loader.SeededTables.Select(Loader.QuoteTable));
        await using var analyze = new NpgsqlCommand($"ANALYZE {analyzeList}", conn) { CommandTimeout = 600 };
        await analyze.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Peak estimate for a reseed: the current database plus the new dataset (README §5.4, measured 533.8 MB at scale 1.0).
    /// In MiB (1024²), rounded up so the fail-closed guard never under-states the peak.</summary>
    public static long PeakEstimateMegabytes(long currentBytes, decimal scale)
    {
        const long MiB = 1024 * 1024;
        return (currentBytes + (long)Math.Ceiling(MeasuredMegabytesAtScale1 * (double)scale * MiB) + MiB - 1) / MiB;
    }

    static long NewDataMegabytes(decimal scale) => (long)(MeasuredMegabytesAtScale1 * (double)scale);

    /// <summary>Maps SQLSTATE 53100 (disk_full) to an actionable message; other errors keep the generic form. Exit code stays 3.</summary>
    public static string DescribeError(Exception e)
    {
        for (var ex = e; ex is not null; ex = ex.InnerException)
            if (ex is PostgresException { SqlState: PostgresErrorCodes.DiskFull })
                return "ERROR: the database ran out of disk space (SQLSTATE 53100, disk_full). The seeding transaction was rolled back; " +
                       "the previous data is kept. A reseed needs about old + new data until COMMIT: compare SEED_PEAK_EST_MB with the " +
                       "storage cap, free space or raise the cap, or seed at a lower --scale (README §10).";
        return $"ERROR: {e.GetType().Name}: {e.Message}";
    }

    public static async Task<long> DatabaseSizeAsync(NpgsqlConnection conn, CancellationToken ct)
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
