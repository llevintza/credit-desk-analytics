// Usage: DATABASE_URL=... dotnet run -c Release --project perf/LoadBenchmark -- [rows=100000]
// Loads the same generated position_history rows into two identical scratch tables (schema `bench`,
// dropped afterwards): once with Npgsql binary COPY, once with EF Core AddRange + SaveChanges.
using System.Diagnostics;
using Desk.Data;
using Desk.Seeder;
using Desk.Seeder.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using NpgsqlTypes;

var rows = args.Length > 0 ? int.Parse(args[0], System.Globalization.CultureInfo.InvariantCulture) : 100_000;
var cs = ConnectionStrings.Resolve(new ConfigurationBuilder().AddEnvironmentVariables().Build(), ConnectionStrings.App);

var asOf = new DateOnly(2026, 10, 6);
var u = Universe.Generate(42, 1.0, asOf);
var data = new Tables(42, 1.0, asOf, u).PositionHistoryRows().Take(rows).ToList();
Console.WriteLine($"rows={data.Count}  shape=core.position_history (10 columns)");

await using var conn = new NpgsqlConnection(cs);
await conn.OpenAsync();
const string ddl = "(as_of_date date, position_id bigint, market_value numeric(18,2), face numeric(18,2), price double precision, spread_bp double precision, dv01 numeric(18,2), cs01 numeric(18,2), wal double precision, pnl_mtd numeric(18,2), PRIMARY KEY (position_id, as_of_date))";
await Exec($"DROP SCHEMA IF EXISTS bench CASCADE; CREATE SCHEMA bench; CREATE TABLE bench.history_copy {ddl}; CREATE TABLE bench.history_ef {ddl};");

try
{
    var copy = Stopwatch.StartNew();
    var alloc0 = GC.GetTotalAllocatedBytes(true);
    Loader.Copy(conn, "bench.history_copy",
        ["as_of_date", "position_id", "market_value", "face", "price", "spread_bp", "dv01", "cs01", "wal", "pnl_mtd"],
        [NpgsqlDbType.Date, NpgsqlDbType.Bigint, NpgsqlDbType.Numeric, NpgsqlDbType.Numeric, NpgsqlDbType.Double,
         NpgsqlDbType.Double, NpgsqlDbType.Numeric, NpgsqlDbType.Numeric, NpgsqlDbType.Double, NpgsqlDbType.Numeric], data);
    copy.Stop();
    var copyAlloc = GC.GetTotalAllocatedBytes(true) - alloc0;

    var options = new DbContextOptionsBuilder<BenchContext>().UseNpgsql(cs).Options;
    var ef = Stopwatch.StartNew();
    alloc0 = GC.GetTotalAllocatedBytes(true);
    await using (var db = new BenchContext(options))
    {
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.Rows.AddRange(data.Select(r => new HistoryRow
        {
            AsOfDate = (DateOnly)r[0]!, PositionId = (long)r[1]!, MarketValue = (decimal)r[2]!, Face = (decimal)r[3]!,
            Price = (double)r[4]!, SpreadBp = (double)r[5]!, Dv01 = (decimal)r[6]!, Cs01 = (decimal)r[7]!, Wal = (double)r[8]!, PnlMtd = (decimal)r[9]!,
        }));
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

async Task Exec(string sql) { await using var c = new NpgsqlCommand(sql, conn); await c.ExecuteNonQueryAsync(); }

sealed class HistoryRow
{
    public DateOnly AsOfDate { get; set; }
    public long PositionId { get; set; }
    public decimal MarketValue { get; set; }
    public decimal Face { get; set; }
    public double Price { get; set; }
    public double SpreadBp { get; set; }
    public decimal Dv01 { get; set; }
    public decimal Cs01 { get; set; }
    public double Wal { get; set; }
    public decimal PnlMtd { get; set; }
}

sealed class BenchContext(DbContextOptions<BenchContext> o) : DbContext(o)
{
    public DbSet<HistoryRow> Rows => Set<HistoryRow>();
    protected override void OnModelCreating(ModelBuilder b) => b.Entity<HistoryRow>(e =>
    {
        e.ToTable("history_ef", "bench");
        e.HasKey(x => new { x.PositionId, x.AsOfDate });
        e.Property(x => x.AsOfDate).HasColumnName("as_of_date");
        e.Property(x => x.PositionId).HasColumnName("position_id");
        e.Property(x => x.MarketValue).HasColumnName("market_value").HasPrecision(18, 2);
        e.Property(x => x.Face).HasColumnName("face").HasPrecision(18, 2);
        e.Property(x => x.Price).HasColumnName("price");
        e.Property(x => x.SpreadBp).HasColumnName("spread_bp");
        e.Property(x => x.Dv01).HasColumnName("dv01").HasPrecision(18, 2);
        e.Property(x => x.Cs01).HasColumnName("cs01").HasPrecision(18, 2);
        e.Property(x => x.Wal).HasColumnName("wal");
        e.Property(x => x.PnlMtd).HasColumnName("pnl_mtd").HasPrecision(18, 2);
    });
}
