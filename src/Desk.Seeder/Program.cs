using System.Diagnostics;
using Desk.Data.App;
using Desk.Seeder;
using Microsoft.EntityFrameworkCore;

// Exit codes: 0 ok (seeded or skipped), 1 bad arguments / not migrated, 2 over the size budget.
SeedOptions options;
try { options = SeedOptions.Parse(args); }
catch (ArgumentException e) { Console.Error.WriteLine($"ERROR: {e.Message}"); return 1; }

var sw = Stopwatch.StartNew();
await using var db = new AppDbContextDesignFactory().CreateDbContext(args);
var ct = CancellationToken.None;

if ((await db.Database.GetPendingMigrationsAsync(ct)).Any())
{
    Console.Error.WriteLine("ERROR: database has pending migrations. Run the migrations bundle first (README §14.2).");
    return 1;
}

if (!options.SizeReportOnly)
{
    var last = await db.SeedMetadata.AsNoTracking().OrderByDescending(m => m.CompletedAt).FirstOrDefaultAsync(ct);
    var upToDate = last is not null && last.Version == SeedVersion.Current && last.Scale == options.Scale && last.Seed == options.Seed;

    if (upToDate && !options.Force)
    {
        Console.WriteLine($"SEED_ACTION=skipped (version {SeedVersion.Current}, scale {options.Scale}, seed {options.Seed} already loaded at {last!.CompletedAt:u})");
    }
    else
    {
        // Phase 0: no generators yet; the run only records metadata so the pipeline is exercised end to end.
        // Phase 1 adds the deterministic generators and binary COPY loads here (README §5.5).
        db.SeedMetadata.Add(new SeedMetadata
        {
            Version = SeedVersion.Current,
            Seed = options.Seed,
            Scale = options.Scale,
            CompletedAt = DateTimeOffset.UtcNow,
            DatabaseSizeBytes = await DatabaseSizeAsync(db, ct),
        });
        await db.SaveChangesAsync(ct);
        Console.WriteLine($"SEED_ACTION=seeded (version {SeedVersion.Current}, scale {options.Scale}, seed {options.Seed}{(options.Force ? ", forced" : "")})");
    }
}

var bytes = await DatabaseSizeAsync(db, ct);
var mb = bytes / 1024 / 1024;
Console.WriteLine($"DB_SIZE_MB={mb}");
Console.WriteLine($"ELAPSED_S={sw.Elapsed.TotalSeconds:F1}");
if (mb > options.MaxMegabytes)
{
    Console.Error.WriteLine($"ERROR: database is {mb} MB, over the {options.MaxMegabytes} MB budget (README §5.4).");
    return 2;
}
return 0;

static async Task<long> DatabaseSizeAsync(AppDbContext db, CancellationToken ct) =>
    await db.Database.SqlQueryRaw<long>("SELECT pg_database_size(current_database()) AS \"Value\"").SingleAsync(ct);
