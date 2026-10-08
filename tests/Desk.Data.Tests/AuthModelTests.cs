using System.Security.Claims;
using Desk.Data;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Desk.Data.Tests;

public sealed class AuthModelTests
{
    [Theory]
    [InlineData("UserName", "user_name")]
    [InlineData("NormalizedEmail", "normalized_email")]
    [InlineData("Id", "id")]
    [InlineData("Xml", "xml")]
    [InlineData("FriendlyName", "friendly_name")]
    [InlineData("SQLText", "sql_text")]
    [InlineData("already_snake", "already_snake")]
    [InlineData("UserID", "user_id")]
    [InlineData("Utf8Value", "utf8value")]
    public void Snake_case_column_names(string name, string expected) =>
        Assert.Equal(expected, AppDbContext.ToSnakeCase(name));

    [Fact]
    public void Identity_tables_live_in_the_auth_schema_with_snake_case_columns()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=localhost").Options);
        var users = db.Model.FindEntityType(typeof(DeskUser))!;
        Assert.Equal(("auth", "users"), (users.GetSchema(), users.GetTableName()));
        Assert.Equal("normalized_email", users.FindProperty(nameof(DeskUser.NormalizedEmail))!.GetColumnName());
        Assert.Equal("expires_at", users.FindProperty(nameof(DeskUser.ExpiresAt))!.GetColumnName());

        var audit = db.Model.FindEntityType(typeof(AuditEntry))!;
        Assert.Equal(("app", "audit"), (audit.GetSchema(), audit.GetTableName()));
        Assert.All(db.Model.GetEntityTypes().Where(t => t.GetSchema() == AppDbContext.AuthSchema),
            t => Assert.All(t.GetProperties(), p => Assert.Equal(p.GetColumnName(), p.GetColumnName().ToLowerInvariant())));
    }

    [Theory]
    [InlineData(false, 1, true)]
    [InlineData(true, 1, false)]
    [InlineData(false, -1, false)]
    [InlineData(false, 0, false)]
    public void A_user_is_active_until_expiry_unless_disabled(bool disabled, int minutesToExpiry, bool expected)
    {
        var now = DateTimeOffset.UnixEpoch;
        var user = new DeskUser { IsDisabled = disabled, ExpiresAt = now.AddMinutes(minutesToExpiry) };
        Assert.Equal(expected, user.IsActiveAt(now));
    }

    [Fact]
    public void Roles_are_viewer_and_admin_only()
    {
        Assert.Equal([Roles.Viewer, Roles.Admin], Roles.All);
        Assert.True(Roles.IsKnown("viewer"));
        Assert.False(Roles.IsKnown("Viewer"));
        Assert.False(Roles.IsKnown(null));
    }

    [Fact]
    public void Expiry_claim_round_trips_and_is_null_when_missing_or_garbage()
    {
        var at = new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero);
        var ok = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DeskClaimsFactory.ExpiresAtClaim, at.ToString("O"))]));
        Assert.Equal(at, DeskClaimsFactory.ReadExpiry(ok));
        Assert.Null(DeskClaimsFactory.ReadExpiry(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(DeskClaimsFactory.ReadExpiry(new ClaimsPrincipal(new ClaimsIdentity([new Claim(DeskClaimsFactory.ExpiresAtClaim, "soon")]))));
    }

    [Fact]
    public void Identity_policy_matches_the_spec()
    {
        var o = new Microsoft.AspNetCore.Identity.IdentityOptions();
        IdentityPolicy.Apply(o);
        Assert.Equal(14, o.Password.RequiredLength);
        Assert.Equal(5, o.Lockout.MaxFailedAccessAttempts);
        Assert.Equal(TimeSpan.FromMinutes(15), o.Lockout.DefaultLockoutTimeSpan);
        Assert.True(o.User.RequireUniqueEmail);
    }

    [Fact]
    public void Connection_counter_counts_sync_and_async_opens()
    {
        var counter = new DbConnectionCounter();
        counter.ConnectionOpened(null!, null!);
        Assert.True(counter.ConnectionOpenedAsync(null!, null!, TestContext.Current.CancellationToken).IsCompletedSuccessfully);
        Assert.Equal(2, counter.Opened);
    }

    [Fact]
    public async Task AddDeskData_registers_a_scoped_context_from_the_pool_with_the_counter_and_a_10s_timeout()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:App"] = "Host=localhost;Database=x;Username=x" })
            .Build();
        var services = new ServiceCollection().AddDeskData(config);
        await using var sp = services.BuildServiceProvider();
        await using var scope = sp.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Same(db, scope.ServiceProvider.GetRequiredService<AppDbContext>());
        Assert.Equal(ServiceCollectionExtensions.CommandTimeoutSeconds, db.Database.GetCommandTimeout());
        Assert.Contains(sp.GetRequiredService<DbConnectionCounter>(), sp.GetServices<IInterceptor>());
    }
}
