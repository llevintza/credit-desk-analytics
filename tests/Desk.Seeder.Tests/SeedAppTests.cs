using System.Security.Cryptography;
using Desk.Data.App;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Desk.Seeder.Tests;

/// <summary>
/// Ports #5's SeedApp tests onto <see cref="SeedRunner"/> / <see cref="SeedApp"/>.
/// A first <c>--if-changed</c> seed must load rows, not just write metadata.
/// </summary>
[Collection(nameof(SeederPostgresCollection))]
public sealed class SeedAppTests
{
    readonly SeederPostgresFixture _pg;

    public SeedAppTests(SeederPostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Bad_arguments_exit_1_before_touching_the_database()
    {
        using var env = Env.Set(("DATABASE_URL", null), ("ConnectionStrings__App", null));
        var code = await SeedApp.RunAsync(["--nope"], TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Pending_migrations_exit_1()
    {
        using var env = Env.Set(("DATABASE_URL", _pg.ConnectionString), ("ConnectionStrings__App", null));
        await ResetAsync(migrate: false);
        var code = await SeedApp.RunAsync(["--if-changed", "--scale", "0.1"], TestContext.Current.CancellationToken);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task Seed_then_if_changed_skip_exits_0_and_loads_rows()
    {
        using var env = Env.Set(("DATABASE_URL", _pg.ConnectionString), ("ConnectionStrings__App", null));
        await ResetAsync(migrate: true);

        var first = await CaptureAsync(["--if-changed", "--scale", "0.1", "--as-of", "2026-10-06"]);
        Assert.Equal(0, first.Code);
        Assert.Contains("SEED_ACTION=seeded", first.Stdout, StringComparison.Ordinal);
        Assert.True(await CountAsync("core.position_snapshot") > 0);
        Assert.True(await CountAsync("core.deal") > 0);
        Assert.True(await CountAsync("app.column_catalog") > 0);
        Assert.True(await CountAsync("app.seed_metadata") > 0);

        var second = await CaptureAsync(["--if-changed", "--scale", "0.1", "--as-of", "2026-10-06"]);
        Assert.Equal(0, second.Code);
        Assert.Contains("SEED_ACTION=skipped", second.Stdout, StringComparison.Ordinal);
        Assert.True(await CountAsync("core.position_snapshot") > 0);
    }

    [Fact]
    public async Task Force_reseeds_after_a_skip_would_have_happened()
    {
        using var env = Env.Set(("DATABASE_URL", _pg.ConnectionString), ("ConnectionStrings__App", null));
        await ResetAsync(migrate: true);
        Assert.Equal(0, (await CaptureAsync(["--if-changed", "--scale", "0.1", "--as-of", "2026-10-06"])).Code);

        var forced = await CaptureAsync(["--force", "--scale", "0.1", "--as-of", "2026-10-06"]);
        Assert.Equal(0, forced.Code);
        Assert.Contains("SEED_ACTION=seeded", forced.Stdout, StringComparison.Ordinal);
        Assert.Contains("forced", forced.Stdout, StringComparison.Ordinal);
        Assert.True(await CountAsync("core.position_snapshot") > 0);
    }

    [Fact]
    public async Task Size_report_does_not_seed()
    {
        using var env = Env.Set(("DATABASE_URL", _pg.ConnectionString), ("ConnectionStrings__App", null));
        await ResetAsync(migrate: true);

        var report = await CaptureAsync(["--size-report"]);
        Assert.Equal(0, report.Code);
        Assert.DoesNotContain("SEED_ACTION=", report.Stdout, StringComparison.Ordinal);
        Assert.Contains("DB_SIZE_MB=", report.Stdout, StringComparison.Ordinal);

        await using var db = new AppDbContextDesignFactory().CreateDbContext([]);
        Assert.Equal(0, await db.SeedMetadata.CountAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, await CountAsync("core.position_snapshot"));
    }

    [Fact]
    public async Task Over_budget_exits_2()
    {
        using var env = Env.Set(("DATABASE_URL", _pg.ConnectionString), ("ConnectionStrings__App", null));
        await ResetAsync(migrate: true);
        var result = await CaptureAsync(["--size-report", "--max-mb", "1"]);
        Assert.Equal(2, result.Code);
        Assert.Contains("over the 1 MB budget", result.Stderr, StringComparison.Ordinal);
    }

    async Task ResetAsync(bool migrate)
    {
        await using var db = new AppDbContextDesignFactory().CreateDbContext([]);
        await db.Database.EnsureDeletedAsync(TestContext.Current.CancellationToken);
        if (migrate)
            await db.Database.MigrateAsync(TestContext.Current.CancellationToken);
    }

    async Task<long> CountAsync(string table)
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        await conn.OpenAsync(TestContext.Current.CancellationToken);
        await using var cmd = new NpgsqlCommand($"SELECT count(*) FROM {table}", conn);
        return (long)(await cmd.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    static async Task<(int Code, string Stdout, string Stderr)> CaptureAsync(string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var oldOut = Console.Out;
        var oldErr = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var code = await SeedApp.RunAsync(args, TestContext.Current.CancellationToken);
            return (code, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(oldOut);
            Console.SetError(oldErr);
        }
    }

    sealed class Env : IDisposable
    {
        readonly List<(string Key, string? Previous)> _previous = [];

        public static Env Set(params (string Key, string? Value)[] pairs)
        {
            var scope = new Env();
            foreach (var (key, value) in pairs)
            {
                scope._previous.Add((key, Environment.GetEnvironmentVariable(key)));
                Environment.SetEnvironmentVariable(key, value);
            }
            return scope;
        }

        public void Dispose()
        {
            foreach (var (key, previous) in _previous)
                Environment.SetEnvironmentVariable(key, previous);
        }
    }
}

[CollectionDefinition(nameof(SeederPostgresCollection), DisableParallelization = true)]
public sealed class SeederPostgresCollection : ICollectionFixture<SeederPostgresFixture>;

public sealed class SeederPostgresFixture : IAsyncLifetime
{
    PostgreSqlContainer? _container;

    public string ConnectionString { get; private set; } = "";

    public async ValueTask InitializeAsync()
    {
        var password = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        // Keep in sync with docker-compose.yml. Dependabot does not scan C# strings.
        // Testcontainers also pulls an unpinned Ryuk helper chosen by this library version.
        _container = new PostgreSqlBuilder("postgres:17-alpine@sha256:b0f9560a2de083e2cc7382e75f808c7381a32852a7ec49117deedb300e552b24")
            .WithDatabase("creditdesk")
            .WithUsername("desk")
            .WithPassword(password)
            .Build();
        await _container.StartAsync();
        ConnectionString = _container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
            await _container.DisposeAsync();
    }
}
