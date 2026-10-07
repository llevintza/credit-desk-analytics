using System.Diagnostics;
using Desk.Data.App;
using Desk.Seeder.Generation;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Desk.Seeder;

/// <summary>The seeding workflow (README §5.5, §14.2), callable from the CLI and from integration tests.</summary>
public static class SeedRunner
{
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

        if (!options.SizeReportOnly)
        {
            string? lastVersion = null; decimal lastScale = 0; int lastSeed = 0; DateTimeOffset lastAt = default;
            await using (var cmd = new NpgsqlCommand(
                "SELECT version, scale, seed, completed_at FROM app.seed_metadata ORDER BY completed_at DESC LIMIT 1", conn))
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                if (await r.ReadAsync(ct)) { lastVersion = r.GetString(0); lastScale = r.GetDecimal(1); lastSeed = r.GetInt32(2); lastAt = r.GetFieldValue<DateTimeOffset>(3); }

            var upToDate = lastVersion == SeedVersion.Current && lastScale == options.Scale && lastSeed == options.Seed;
            if (upToDate && options.IfChanged)
            {
                output.WriteLine($"SEED_ACTION=skipped (version {SeedVersion.Current}, scale {options.Scale}, seed {options.Seed} already loaded at {lastAt:u})");
            }
            else
            {
                var gen = Stopwatch.StartNew();
                var universe = Universe.Generate(options.Seed, (double)options.Scale, options.AsOf);
                var tables = new Tables(options.Seed, (double)options.Scale, options.AsOf, universe);
                output.WriteLine($"Generating as of {options.AsOf:yyyy-MM-dd} (prior business day {tables.PriorBusinessDay:yyyy-MM-dd}): " +
                                  $"{universe.Deals.Count} deals, {universe.Bonds.Count} bonds, {tables.Positions.Count} positions");

                // One transaction: a failed reseed leaves the previous data in place (README §14.3).
                await using (var tx = await conn.BeginTransactionAsync(ct))
                {
                    var stats = Loader.LoadAll(conn, universe, tables, ct);
                    foreach (var s in stats) output.WriteLine($"  {s.Table,-28} {s.Rows,10:N0} rows  {s.Elapsed.TotalSeconds,6:F1} s");
                    await using (var ins = new NpgsqlCommand(
                        "INSERT INTO app.seed_metadata (version, seed, scale, completed_at, database_size_bytes) VALUES (@v, @s, @sc, now(), 0)", conn, tx))
                    {
                        ins.Parameters.AddWithValue("v", SeedVersion.Current);
                        ins.Parameters.AddWithValue("s", options.Seed);
                        ins.Parameters.AddWithValue("sc", options.Scale);
                        await ins.ExecuteNonQueryAsync(ct);
                    }
                    await tx.CommitAsync(ct);
                }
                await using (var analyze = new NpgsqlCommand("ANALYZE", conn) { CommandTimeout = 600 }) await analyze.ExecuteNonQueryAsync(ct);
                await using (var upd = new NpgsqlCommand(
                    "UPDATE app.seed_metadata SET database_size_bytes = pg_database_size(current_database()) WHERE id = (SELECT max(id) FROM app.seed_metadata)", conn))
                    await upd.ExecuteNonQueryAsync(ct);
                output.WriteLine($"SEED_ACTION=seeded (version {SeedVersion.Current}, scale {options.Scale}, seed {options.Seed}, as of {options.AsOf:yyyy-MM-dd}{(options.Force ? ", forced" : "")}) in {gen.Elapsed.TotalSeconds:F1} s");
            }
        }

        long bytes;
        await using (var size = new NpgsqlCommand("SELECT pg_database_size(current_database())", conn))
            bytes = (long)(await size.ExecuteScalarAsync(ct))!;
        var mb = bytes / 1024 / 1024;
        output.WriteLine($"DB_SIZE_MB={mb}");
        output.WriteLine($"ELAPSED_S={total.Elapsed.TotalSeconds:F1}");
        if (mb > options.MaxMegabytes)
        {
            err.WriteLine($"ERROR: database is {mb} MB, over the {options.MaxMegabytes} MB budget (README §5.4).");
            return 2;
        }
        return 0;
    }
}
