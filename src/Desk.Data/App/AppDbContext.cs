using Microsoft.EntityFrameworkCore;

namespace Desk.Data.App;

/// <summary>Stable application model (EF Core): seed metadata and the column catalog; accounts, presets and audit in later phases.</summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const string Schema = "app";

    public DbSet<SeedMetadata> SeedMetadata => Set<SeedMetadata>();
    public DbSet<ColumnCatalogEntry> ColumnCatalog => Set<ColumnCatalogEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema(Schema);
        b.Entity<SeedMetadata>(e =>
        {
            e.ToTable("seed_metadata");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.Version).HasColumnName("version").HasMaxLength(64);
            e.Property(x => x.Seed).HasColumnName("seed");
            e.Property(x => x.Scale).HasColumnName("scale").HasPrecision(6, 3);
            e.Property(x => x.CompletedAt).HasColumnName("completed_at");
            e.Property(x => x.DatabaseSizeBytes).HasColumnName("database_size_bytes");
            e.HasIndex(x => x.CompletedAt);
        });
        b.Entity<ColumnCatalogEntry>(e =>
        {
            e.ToTable("column_catalog");
            e.HasKey(x => x.Name);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(64);
            e.Property(x => x.Ordinal).HasColumnName("ordinal");
            e.Property(x => x.Group).HasColumnName("group_name").HasMaxLength(64);
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(x => x.Aggregation).HasColumnName("aggregation").HasMaxLength(32);
            e.Property(x => x.Header).HasColumnName("header").HasMaxLength(64);
            e.HasIndex(x => x.Ordinal).IsUnique();
        });
    }
}
