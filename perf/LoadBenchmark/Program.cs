// Usage: DATABASE_URL=... dotnet run -c Release --project perf/LoadBenchmark -- [rows=20000]
// Loads the same generated core.position_snapshot rows into two identical scratch tables
// (schema `bench`, dropped afterwards): once with Npgsql binary COPY, once with EF Core
// AddRange + SaveChanges. README §5.5 requires this comparison on the snapshot table.
using System.Diagnostics;
using Desk.Data;
using Desk.Data.Catalog;
using Desk.Seeder;
using Desk.Seeder.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

var rows = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 20_000;
var cs = ConnectionStrings.Resolve(new ConfigurationBuilder().AddEnvironmentVariables().Build(), ConnectionStrings.App);

var asOf = new DateOnly(2026, 10, 6);
var u = Universe.Generate(42, 1.0, asOf);
var data = new Tables(42, 1.0, asOf, u).PositionSnapshotRows(asOf).Take(rows).ToList();
var cols = ColumnCatalog.PositionSnapshot;
Console.WriteLine($"rows={data.Count}  shape=core.position_snapshot ({cols.Count} columns)");

await using var conn = new NpgsqlConnection(cs);
await conn.OpenAsync();
var colDdl = string.Join(",\n    ", cols.Select(c =>
{
    var notNull = c.Kind == ColumnKind.Key || c.Name is "as_of_date" or "cusip" or "deal_name" or "sector";
    return $"{c.Name} {c.SqlType}{(notNull ? " NOT NULL" : "")}";
}));
var ddl = $"(\n    {colDdl},\n    PRIMARY KEY (as_of_date, position_id)\n)";
await Exec($"DROP SCHEMA IF EXISTS bench CASCADE; CREATE SCHEMA bench; CREATE TABLE bench.snapshot_copy {ddl}; CREATE TABLE bench.snapshot_ef {ddl};");

try
{
    var copy = Stopwatch.StartNew();
    var alloc0 = GC.GetTotalAllocatedBytes(true);
    Loader.Copy(conn, "bench.snapshot_copy",
        cols.Select(c => c.Name).ToArray(),
        cols.Select(Loader.DbType).ToArray(), data);
    copy.Stop();
    var copyAlloc = GC.GetTotalAllocatedBytes(true) - alloc0;

    var options = new DbContextOptionsBuilder<BenchContext>().UseNpgsql(cs).Options;
    var ef = Stopwatch.StartNew();
    alloc0 = GC.GetTotalAllocatedBytes(true);
    await using (var db = new BenchContext(options))
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.Set<Dictionary<string, object>>("Snap").AddRange(data.Select(ToBag));
        await db.SaveChangesAsync();
    }
    ef.Stop();
    var efAlloc = GC.GetTotalAllocatedBytes(true) - alloc0;

    Console.WriteLine($"| Method | Rows | Seconds | Rows/s | Managed alloc (MB) |");
    Console.WriteLine($"|---|---|---|---|---|");
    Console.WriteLine($"| Npgsql binary COPY | {data.Count:N0} | {copy.Elapsed.TotalSeconds:F2} | {data.Count / copy.Elapsed.TotalSeconds:N0} | {copyAlloc / 1048576.0:F0} |");
    Console.WriteLine($"| EF Core AddRange + SaveChanges | {data.Count:N0} | {ef.Elapsed.TotalSeconds:F2} | {data.Count / ef.Elapsed.TotalSeconds:N0} | {efAlloc / 1048576.0:F0} |");
    Console.WriteLine($"COPY is {ef.Elapsed.TotalSeconds / copy.Elapsed.TotalSeconds:F1}x faster");
}
finally
{
    await Exec("DROP SCHEMA IF EXISTS bench CASCADE;");
}

Dictionary<string, object> ToBag(object?[] r)
{
    var bag = new Dictionary<string, object>(cols.Count);
    for (var i = 0; i < cols.Count; i++)
        if (r[i] is not null) bag[cols[i].Name] = r[i]!;
    return bag;
}

async Task Exec(string sql) { await using var c = new NpgsqlCommand(sql, conn); await c.ExecuteNonQueryAsync(); }

sealed class BenchContext(DbContextOptions<BenchContext> o) : DbContext(o)
{
    protected override void OnModelCreating(ModelBuilder b) =>
        b.SharedTypeEntity<Dictionary<string, object>>("Snap", e =>
        {
            e.ToTable("snapshot_ef", "bench");
            foreach (var c in ColumnCatalog.PositionSnapshot)
                e.Property(ClrType(c), c.Name);
            e.HasKey("as_of_date", "position_id");
        });

    static Type ClrType(ColumnDef c) => c.Kind switch
    {
        ColumnKind.Key => c.Name == "position_id" ? typeof(long) : typeof(int),
        ColumnKind.Text => typeof(string),
        ColumnKind.Date => typeof(DateOnly),
        ColumnKind.Money => typeof(decimal),
        ColumnKind.Count => typeof(int),
        ColumnKind.Flag => typeof(bool),
        _ => typeof(double),
    };
}
