using Desk.Data;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Desk.Data.Tests;

[Collection(nameof(ProcessEnvironmentCollection))]
public sealed class AppDbContextDesignFactoryTests
{
    [Fact]
    public void CreateDbContext_reads_DATABASE_URL()
    {
        using var env = ProcessEnvironment.Set(
            ("DATABASE_URL", "Host=localhost;Database=creditdesk;Username=desk"),
            ("ConnectionStrings__App", null));

        using var ctx = new AppDbContextDesignFactory().CreateDbContext([]);
        var cs = ctx.Database.GetConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(cs));
        var b = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("localhost", b.Host);
        Assert.Equal("creditdesk", b.Database);
        Assert.Equal("desk", b.Username);
    }

    [Fact]
    public void CreateDbContext_prefers_ConnectionStrings_App()
    {
        using var env = ProcessEnvironment.Set(
            ("DATABASE_URL", "Host=fallback;Database=fallback;Username=desk"),
            ("ConnectionStrings__App", "Host=app-host;Database=app;Username=desk"));

        using var ctx = new AppDbContextDesignFactory().CreateDbContext(["ignored"]);
        var cs = ctx.Database.GetConnectionString();
        Assert.False(string.IsNullOrWhiteSpace(cs));
        var b = new NpgsqlConnectionStringBuilder(cs);
        Assert.Equal("app-host", b.Host);
        Assert.Equal("app", b.Database);
        Assert.Equal("desk", b.Username);
    }

    [Fact]
    public void CreateDbContext_throws_when_no_connection_is_configured()
    {
        using var env = ProcessEnvironment.Set(
            ("DATABASE_URL", null),
            ("ConnectionStrings__App", null));

        var ex = Assert.Throws<InvalidOperationException>(() =>
            new AppDbContextDesignFactory().CreateDbContext([]));
        Assert.Contains("App", ex.Message, StringComparison.Ordinal);
    }
}
