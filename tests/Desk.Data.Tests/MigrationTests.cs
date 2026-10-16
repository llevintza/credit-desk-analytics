using Desk.Data.App.Migrations;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Desk.Data.Tests;

/// <summary>Migrations that run raw SQL on deploy (README §14.2/§14.4).</summary>
public sealed class MigrationTests
{
    [Fact]
    public void The_sort_index_migration_is_rerun_safe_both_ways()
    {
        // #130 N3: a retried deploy, or an index someone created by hand, must not fail the migration step.
        var migration = new SnapshotSortIndexes();
        var up = migration.UpOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();
        var down = migration.DownOperations.OfType<SqlOperation>().Select(o => o.Sql).ToList();

        Assert.Equal(SnapshotSortIndexes.SortColumns.Length, up.Count);
        Assert.All(up, sql => Assert.StartsWith("CREATE INDEX IF NOT EXISTS ix_snapshot_sort_", sql));
        Assert.Equal(up.Count, down.Count); // else Assert.All(down, …) passes on an empty Down
        Assert.All(down, sql => Assert.StartsWith("DROP INDEX IF EXISTS core.ix_snapshot_sort_", sql));
        Assert.Equal(SnapshotSortIndexes.SortColumns, up.Select(sql => sql.Split(' ')[5]["ix_snapshot_sort_".Length..]));
    }

    [Fact]
    public void The_NaN_checks_cover_exactly_the_summarised_columns()
    {
        // #192: every column the summary row SUMs or weights, plus the weight itself. A new measure in the catalog fails
        // this until a new migration guards it (the frozen lists here must not change once applied).
        var summarised = ColumnCatalog.PositionSnapshot.Where(c => c.Aggregation != Aggregation.None).ToList();
        Assert.Equal(summarised.Where(c => c.SqlType == "numeric(18,2)").Select(c => c.Name), SnapshotNanChecks.NumericMeasures);
        Assert.Equal(summarised.Where(c => c.SqlType == "double precision").Select(c => c.Name), SnapshotNanChecks.Float8Measures);
        Assert.Equal(summarised.Count, SnapshotNanChecks.NumericMeasures.Length + SnapshotNanChecks.Float8Measures.Length);
        Assert.Contains(GridSqlBuilder.WeightColumn, SnapshotNanChecks.NumericMeasures);
        Assert.Contains(GridSqlBuilder.FaceWeightColumn, SnapshotNanChecks.NumericMeasures); // #459: the price weight
    }

    [Fact]
    public void The_NaN_check_migration_is_rerun_safe_and_names_every_constraint_alike()
    {
        var migration = new SnapshotNanChecks();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>()).Sql;
        var columns = SnapshotNanChecks.NumericMeasures.Concat(SnapshotNanChecks.Float8Measures).ToList();

        Assert.StartsWith("ALTER TABLE core.position_snapshot\n", up);
        Assert.StartsWith("ALTER TABLE core.position_snapshot\n", down);
        foreach (var c in columns)
        {
            var name = SnapshotNanChecks.ConstraintName(c);
            Assert.Equal($"ck_snapshot_{c}_not_nan", name);
            Assert.True(name.Length <= 63, name); // Postgres truncates longer identifiers
            var type = SnapshotNanChecks.NumericMeasures.Contains(c) ? "numeric" : "float8";
            Assert.Contains($"DROP CONSTRAINT IF EXISTS {name}, ADD CONSTRAINT {name} CHECK ({c} <> 'NaN'::{type})", up);
            Assert.Contains($"DROP CONSTRAINT IF EXISTS {name}", down);
        }
        Assert.Equal(columns.Count, up.Split("ADD CONSTRAINT").Length - 1);
        Assert.Equal(columns.Count, down.Split("DROP CONSTRAINT IF EXISTS").Length - 1);
        Assert.DoesNotContain("ADD CONSTRAINT", down);
    }

    [Fact]
    public void The_finite_check_migration_replaces_the_float8_NaN_checks_rerun_safely()
    {
        // #304: exactly the float8 measures, each _not_nan check swapped for a _finite one; Down swaps them back.
        var migration = new SnapshotFiniteChecks();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>()).Sql;

        Assert.Equal(SnapshotNanChecks.Float8Measures, SnapshotFiniteChecks.Columns);
        Assert.StartsWith("ALTER TABLE core.position_snapshot\n", up);
        Assert.StartsWith("ALTER TABLE core.position_snapshot\n", down);
        foreach (var c in SnapshotFiniteChecks.Columns)
        {
            var finite = SnapshotFiniteChecks.ConstraintName(c);
            var notNan = SnapshotNanChecks.ConstraintName(c);
            Assert.Equal($"ck_snapshot_{c}_finite", finite);
            Assert.True(finite.Length <= 63, finite); // Postgres truncates longer identifiers
            Assert.Contains($"DROP CONSTRAINT IF EXISTS {notNan}, DROP CONSTRAINT IF EXISTS {finite}, " +
                            $"ADD CONSTRAINT {finite} CHECK ({c} > '-Infinity'::float8 AND {c} < 'Infinity'::float8)", up);
            Assert.Contains($"DROP CONSTRAINT IF EXISTS {finite}, DROP CONSTRAINT IF EXISTS {notNan}, " +
                            $"ADD CONSTRAINT {notNan} CHECK ({c} <> 'NaN'::float8)", down);
        }
        Assert.Equal(SnapshotFiniteChecks.Columns.Count, up.Split("ADD CONSTRAINT").Length - 1);
        Assert.Equal(SnapshotFiniteChecks.Columns.Count, down.Split("ADD CONSTRAINT").Length - 1);
        Assert.DoesNotContain("numeric", up);
    }

    [Fact]
    public void The_weight_column_migration_is_rerun_safe_and_matches_the_builder()
    {
        // #241: the stored weight is the builder's weight, under a name no catalog column can take.
        var migration = new SnapshotWeightColumn();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>()).Sql;

        Assert.Equal(GridSqlBuilder.WeightF8Column, SnapshotWeightColumn.Column);
        Assert.Equal(GridSqlBuilder.WeightExpression, SnapshotWeightColumn.Expression);
        Assert.Equal("ALTER TABLE core.position_snapshot ADD COLUMN IF NOT EXISTS weight_f8 double precision " +
                     "GENERATED ALWAYS AS (abs(market_value)::float8) STORED;", up);
        Assert.Equal("ALTER TABLE core.position_snapshot DROP COLUMN IF EXISTS weight_f8;", down);
        Assert.DoesNotContain(ColumnCatalog.PositionSnapshot, c => c.Name == GridSqlBuilder.WeightF8Column);
    }

    [Fact]
    public void The_face_weight_column_migration_is_rerun_safe_and_matches_the_builder()
    {
        // #459: the price weight is stored like the |MV| weight (#241), under a name no catalog column can take.
        var migration = new SnapshotFaceWeightColumn();
        var up = Assert.Single(migration.UpOperations.OfType<SqlOperation>()).Sql;
        var down = Assert.Single(migration.DownOperations.OfType<SqlOperation>()).Sql;

        Assert.Equal(GridSqlBuilder.FaceWeightF8Column, SnapshotFaceWeightColumn.Column);
        Assert.Equal(GridSqlBuilder.FaceWeightExpression, SnapshotFaceWeightColumn.Expression);
        Assert.Equal("ALTER TABLE core.position_snapshot ADD COLUMN IF NOT EXISTS face_weight_f8 double precision " +
                     "GENERATED ALWAYS AS (abs(current_face)::float8) STORED;", up);
        Assert.Equal("ALTER TABLE core.position_snapshot DROP COLUMN IF EXISTS face_weight_f8;", down);
        Assert.DoesNotContain(ColumnCatalog.PositionSnapshot, c => c.Name == GridSqlBuilder.FaceWeightF8Column);
    }

    [Fact]
    public void The_catalog_flip_names_exactly_the_face_weighted_columns()
    {
        // #459 step 2: the frozen list is the code catalog's face-weighted columns, so the served catalog and the code
        // can't drift. A new price column fails this until a new migration flips it (this list must not change).
        Assert.Equal(
            ColumnCatalog.PositionSnapshot.Where(c => c.Aggregation == Aggregation.WeightedByCurrentFace).Select(c => c.Name).Order(),
            CatalogFaceWeightedPrices.Columns.Order());
        Assert.Equal(CatalogFaceWeightedPrices.Columns.Count, CatalogFaceWeightedPrices.Columns.Distinct().Count());
        Assert.All(ColumnCatalog.PositionSnapshot.Where(c => CatalogFaceWeightedPrices.Columns.Contains(c.Name)),
            c => Assert.Equal(ColumnKind.Price, c.Kind));
        Assert.Equal(nameof(Aggregation.WeightedByCurrentFace), CatalogFaceWeightedPrices.FaceWeighted);
        Assert.Equal(nameof(Aggregation.WeightedByMarketValue), CatalogFaceWeightedPrices.MarketValueWeighted);
    }

    [Fact]
    public void The_catalog_flip_is_a_guarded_update_both_ways_with_no_DDL()
    {
        var migration = new CatalogFaceWeightedPrices();
        var up = Assert.IsType<SqlOperation>(Assert.Single(migration.UpOperations)).Sql;
        var down = Assert.IsType<SqlOperation>(Assert.Single(migration.DownOperations)).Sql;
        var names = string.Join(", ", CatalogFaceWeightedPrices.Columns.Select(c => $"'{c}'"));

        Assert.Equal($"UPDATE app.column_catalog SET aggregation = 'WeightedByCurrentFace' WHERE aggregation <> 'WeightedByCurrentFace' AND name IN ({names});", up);
        Assert.Equal($"UPDATE app.column_catalog SET aggregation = 'WeightedByMarketValue' WHERE aggregation <> 'WeightedByMarketValue' AND name IN ({names});", down);
        Assert.All(new[] { up, down }, sql =>
        {
            Assert.DoesNotContain("ALTER", sql);
            Assert.DoesNotContain("position_snapshot", sql);
            Assert.Single(sql.Split(';', StringSplitOptions.RemoveEmptyEntries));
        });
    }
}
