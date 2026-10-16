using System.Text;
using Desk.Data.Auth;
using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Desk.Data.App;

/// <summary>
/// Stable application model (EF Core): seed metadata, the column catalog and the audit log in <c>app</c>;
/// accounts (ASP.NET Core Identity) and data-protection keys in <c>auth</c> (README §7.1). One context, so one
/// migrations history and one bundle for the deploy pipeline. The seeder never truncates either schema.
/// </summary>
public sealed class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext<DeskUser, IdentityRole<Guid>, Guid>(options), IDataProtectionKeyContext
{
    public const string Schema = "app";
    public const string AuthSchema = "auth";

    public DbSet<SeedMetadata> SeedMetadata => Set<SeedMetadata>();
    public DbSet<ColumnCatalogEntry> ColumnCatalog => Set<ColumnCatalogEntry>();
    public DbSet<AuditEntry> Audit => Set<AuditEntry>();
    public DbSet<Preset> Presets => Set<Preset>();
    public DbSet<PortfolioGrant> PortfolioGrants => Set<PortfolioGrant>();
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();
    public DbSet<AuthSession> AuthSessions => Set<AuthSession>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);
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
        b.Entity<AuditEntry>(e =>
        {
            e.ToTable("audit");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.At).HasColumnName("at");
            e.Property(x => x.Kind).HasColumnName("kind").HasMaxLength(16);
            e.Property(x => x.UserName).HasColumnName("user_name").HasMaxLength(256);
            e.Property(x => x.Endpoint).HasColumnName("endpoint").HasMaxLength(256);
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.Rows).HasColumnName("rows");
            e.Property(x => x.Ms).HasColumnName("ms");
            e.Property(x => x.Cache).HasColumnName("cache").HasMaxLength(8);
            // The Usage page reads "per user per day" and "slowest today": both start from a time range.
            e.HasIndex(x => x.At);
        });

        b.Entity<Preset>(e =>
        {
            e.ToTable("preset");
            e.Property(x => x.Id).HasColumnName("id");
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.Page).HasColumnName("page").HasMaxLength(32);
            e.Property(x => x.Name).HasColumnName("name").HasMaxLength(64);
            e.Property(x => x.State).HasColumnName("state").HasColumnType("jsonb");
            e.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            e.HasIndex(x => new { x.UserId, x.Page, x.Name }).IsUnique();
            // A deleted account takes its presets with it.
            e.HasOne<DeskUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ADR-0021: per-user portfolio grants. The only FK is to auth.users; core.portfolio is truncated by the seeder
        // and may move to another database.
        b.Entity<PortfolioGrant>(e =>
        {
            e.ToTable("portfolio_grant");
            e.HasKey(x => new { x.UserId, x.PortfolioId });
            e.Property(x => x.UserId).HasColumnName("user_id");
            e.Property(x => x.PortfolioId).HasColumnName("portfolio_id");
            e.Property(x => x.GrantedAt).HasColumnName("granted_at");
            e.Property(x => x.GrantedBy).HasColumnName("granted_by").HasMaxLength(256);
            e.HasIndex(x => x.PortfolioId);
            // A deleted account takes its grants with it.
            e.HasOne<DeskUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<DeskUser>(e =>
        {
            e.ToTable("users", AuthSchema);
            e.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            e.Property(x => x.IsDisabled).HasColumnName("is_disabled");
        });
        b.Entity<IdentityRole<Guid>>(e =>
        {
            e.ToTable("roles", AuthSchema);
            e.HasData(
                new IdentityRole<Guid> { Id = Auth.Roles.ViewerId, Name = Auth.Roles.Viewer, NormalizedName = "VIEWER", ConcurrencyStamp = "viewer" },
                new IdentityRole<Guid> { Id = Auth.Roles.AdminId, Name = Auth.Roles.Admin, NormalizedName = "ADMIN", ConcurrencyStamp = "admin" });
        });
        b.Entity<IdentityUserRole<Guid>>().ToTable("user_roles", AuthSchema);
        b.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", AuthSchema);
        b.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", AuthSchema);
        b.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", AuthSchema);
        b.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims", AuthSchema);
        b.Entity<DataProtectionKey>().ToTable("data_protection_keys", AuthSchema);
        b.Entity<AuthSession>(e =>
        {
            // Logout revocations and refresh rotation (#354, ADR-0024): read and written only at login, refresh and
            // logout, never per request.
            e.ToTable("sessions", AuthSchema);
            e.HasKey(x => x.Sid);
            e.Property(x => x.Sid).HasMaxLength(32);
            e.Property(x => x.Rid).HasMaxLength(32);
            e.Property(x => x.PreviousRid).HasMaxLength(32);
            // Every sign-in deletes the sessions past their 24 h limit.
            e.HasIndex(x => x.ExpiresAt);
        });

        // Identity's own columns are PascalCase; keep the database snake_case like every other table.
        foreach (var entity in b.Model.GetEntityTypes().Where(t => t.GetSchema() == AuthSchema))
            foreach (var property in entity.GetProperties())
                property.SetColumnName(ToSnakeCase(property.Name));
    }

    internal static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c))
            {
                // Break before an upper-case letter that starts a word: "UserName" -> user_name, "FriendlyName" -> friendly_name.
                if (i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]) && char.IsUpper(name[i - 1]))))
                    sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
