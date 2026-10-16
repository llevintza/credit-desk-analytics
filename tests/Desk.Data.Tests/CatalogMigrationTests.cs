using Desk.Data.App.Migrations;
using Desk.Data.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace Desk.Data.Tests;

/// <summary>
/// #459 step 2: <see cref="CatalogFaceWeightedPrices"/> on a catalog seeded before #473 (production's, SeedVersion
/// unchanged): Up makes the served catalog the code catalog, Down puts the |MV| weight back, and both rerun as no-ops.
/// Own database (class fixture), since it migrates down and up.
/// </summary>
public sealed class CatalogMigrationTests(MigratedDatabase db) : IClassFixture<MigratedDatabase>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // What a seed before #473 wrote: the code catalog with every price still |MV|-weighted.
    private static readonly (string Name, string Aggregation)[] PreFaceWeightSeed =
    [
        .. ColumnCatalog.PositionSnapshot.Select(c => (c.Name,
            (c.Aggregation == Aggregation.WeightedByCurrentFace ? Aggregation.WeightedByMarketValue : c.Aggregation).ToString())),
    ];

    private static readonly (string Name, string Aggregation)[] CodeCatalog =
        [.. ColumnCatalog.PositionSnapshot.Select(c => (c.Name, c.Aggregation.ToString()))];

    [Fact]
    public async Task Up_flips_a_pre_473_catalog_to_the_code_catalog_and_Down_restores_it()
    {
        await using var conn = await db.OpenAsync();
        await SeedPreFaceWeightCatalogAsync(conn);
        Assert.Equal(PreFaceWeightSeed, await CatalogAsync(conn));
        var snapshotFile = await ScalarAsync<uint>(conn, "SELECT pg_relation_filenode('core.position_snapshot')::oid");

        // The database was migrated before the rows existed (deploy migrates before it seeds), so step back over the
        // migration and run its Up on real rows: before, every price is |MV|-weighted; after, the catalog is the code's.
        await MigrateToAsync(nameof(SnapshotFaceWeightColumn));
        Assert.Equal(PreFaceWeightSeed, await CatalogAsync(conn));
        await MigrateToAsync(null);
        Assert.Equal(CodeCatalog, await CatalogAsync(conn));
        Assert.Equal(snapshotFile, await ScalarAsync<uint>(conn, "SELECT pg_relation_filenode('core.position_snapshot')::oid")); // no rewrite

        // Idempotent: a rerun writes no row.
        Assert.Equal(0, await ExecuteAsync(conn, CatalogFaceWeightedPrices.SetAggregation(CatalogFaceWeightedPrices.FaceWeighted)));

        // Down restores the |MV| weight on exactly those rows and reruns as a no-op too.
        await MigrateToAsync(nameof(SnapshotFaceWeightColumn));
        Assert.Equal(PreFaceWeightSeed, await CatalogAsync(conn));
        Assert.Equal(0, await ExecuteAsync(conn, CatalogFaceWeightedPrices.SetAggregation(CatalogFaceWeightedPrices.MarketValueWeighted)));

        await MigrateToAsync(null);
        Assert.Equal(CodeCatalog, await CatalogAsync(conn));
    }

    [Fact]
    public async Task Up_writes_only_the_rows_that_differ()
    {
        // A partly flipped catalog (a retried deploy, a row fixed by hand): Up writes the rest and leaves every other row alone.
        await using var conn = await db.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync(Ct);
        await ExecuteAsync(conn, "DELETE FROM app.column_catalog", tx);
        await InsertAsync(conn, tx, ("price", "WeightedByCurrentFace"), ("book_price", "WeightedByMarketValue"),
            ("spread_bp", "WeightedByMarketValue"), ("market_value", "Sum"));

        Assert.Equal(1, await ExecuteAsync(conn, CatalogFaceWeightedPrices.SetAggregation(CatalogFaceWeightedPrices.FaceWeighted), tx));
        Assert.Equal(
            [("book_price", "WeightedByCurrentFace"), ("market_value", "Sum"), ("price", "WeightedByCurrentFace"), ("spread_bp", "WeightedByMarketValue")],
            await CatalogAsync(conn, tx, "name"));
        await tx.RollbackAsync(Ct);
    }

    private async Task MigrateToAsync(string? target)
    {
        await using var context = db.NewContext();
        await context.GetService<IMigrator>().MigrateAsync(target, Ct);
    }

    private static async Task SeedPreFaceWeightCatalogAsync(NpgsqlConnection conn)
    {
        await ExecuteAsync(conn, "DELETE FROM app.column_catalog");
        await InsertAsync(conn, null, PreFaceWeightSeed);
    }

    private static async Task InsertAsync(NpgsqlConnection conn, NpgsqlTransaction? tx, params (string Name, string Aggregation)[] rows)
    {
        // Group, kind and header from the code catalog; only the aggregation varies.
        var defs = ColumnCatalog.PositionSnapshot.ToDictionary(c => c.Name);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO app.column_catalog (name, ordinal, group_name, kind, aggregation, header) " +
            "SELECT * FROM unnest(@names, @ordinals, @groups, @kinds, @aggs, @headers)", conn, tx);
        cmd.Parameters.AddWithValue("names", rows.Select(r => r.Name).ToArray());
        cmd.Parameters.AddWithValue("ordinals", rows.Select(r => ColumnCatalog.PositionSnapshot.ToList().FindIndex(c => c.Name == r.Name)).ToArray());
        cmd.Parameters.AddWithValue("groups", rows.Select(r => defs[r.Name].Group).ToArray());
        cmd.Parameters.AddWithValue("kinds", rows.Select(r => defs[r.Name].Kind.ToString()).ToArray());
        cmd.Parameters.AddWithValue("aggs", rows.Select(r => r.Aggregation).ToArray());
        cmd.Parameters.AddWithValue("headers", rows.Select(r => defs[r.Name].Header).ToArray());
        await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<(string Name, string Aggregation)[]> CatalogAsync(NpgsqlConnection conn, NpgsqlTransaction? tx = null, string orderBy = "ordinal")
    {
        await using var cmd = new NpgsqlCommand($"SELECT name, aggregation FROM app.column_catalog ORDER BY {orderBy}", conn, tx);
        await using var r = await cmd.ExecuteReaderAsync(Ct);
        var rows = new List<(string, string)>();
        while (await r.ReadAsync(Ct))
            rows.Add((r.GetString(0), r.GetString(1)));
        return [.. rows];
    }

    private static async Task<int> ExecuteAsync(NpgsqlConnection conn, string sql, NpgsqlTransaction? tx = null)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        return await cmd.ExecuteNonQueryAsync(Ct);
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        return (T)(await cmd.ExecuteScalarAsync(Ct))!;
    }
}
