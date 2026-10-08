using System.Data.Common;
using System.Net;
using Desk.Api.Auth;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Desk.Api.Tests;

/// <summary>README §7.1: optional demo accounts from DEMO_ACCOUNTS_JSON, created on first login.</summary>
[Collection(ApiCollection.Name)]
public sealed class DemoAccountsTests(PostgresApiFactory api)
{
    [Fact]
    public async Task A_configured_demo_account_can_log_in_and_is_created_once()
    {
        var email = $"demo-{Guid.NewGuid():N}@example.com";
        var json = $$"""[{"email":"{{email}}","password":"{{PostgresApiFactory.Password}}","role":"viewer","expires":"2099-01-01T00:00:00Z"}]""";
        await using var host = api.WithSettings((DemoAccounts.ConfigKey, json));

        await PostgresApiFactory.LoginAsync(PostgresApiFactory.NewClient(host), email);
        await PostgresApiFactory.LoginAsync(PostgresApiFactory.NewClient(host), email);

        await using var db = api.NewContext();
        Assert.Equal(1, db.Users.Count(u => u.Email == email));
    }

    [Fact]
    public async Task A_demo_account_whose_password_breaks_the_policy_is_not_created()
    {
        var email = $"demo-{Guid.NewGuid():N}@example.com";
        var json = $$"""[{"email":"{{email}}","password":"short","role":"viewer","expires":"2099-01-01T00:00:00Z"}]""";
        await using var host = api.WithSettings((DemoAccounts.ConfigKey, json));

        var res = await PostgresApiFactory.PostLoginAsync(PostgresApiFactory.NewClient(host), email, "short");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        await using var db = api.NewContext();
        Assert.False(db.Users.Any(u => u.Email == email));
    }

    [Fact]
    public async Task Concurrent_first_logins_create_one_account_and_all_succeed()
    {
        var email = $"demo-{Guid.NewGuid():N}@example.com";
        var json = $$"""[{"email":"{{email}}","password":"{{PostgresApiFactory.Password}}","role":"admin","expires":"2099-01-01T00:00:00+02:00"}]""";
        await using var host = api.WithSettings((DemoAccounts.ConfigKey, json));

        var logins = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => PostgresApiFactory.PostLoginAsync(PostgresApiFactory.NewClient(host), email)));
        Assert.All(logins, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        await using var db = api.NewContext();
        var user = Assert.Single(db.Users.Where(u => u.Email == email));
        // A non-UTC offset in the secret is stored as the same instant in UTC.
        Assert.Equal(new DateTimeOffset(2098, 12, 31, 22, 0, 0, TimeSpan.Zero), user.ExpiresAt);
    }

    [Fact]
    public async Task Losing_the_insert_race_leaves_nothing_stale_for_the_request_s_next_save()
    {
        // The account is created by "another request" just as this one inserts it, so the insert hits the unique
        // index. A wrong password then saves a failed-attempt count in the same request: that save must not retry
        // the stale insert (it did: a 500).
        var email = $"demo-{Guid.NewGuid():N}@example.com";
        var json = $$"""[{"email":"{{email}}","password":"{{PostgresApiFactory.Password}}","role":"viewer","expires":"2099-01-01T00:00:00Z"}]""";
        var race = new UserInsertRace(api, email);
        await using var host = api.WithWebHostBuilder(b =>
        {
            b.UseSetting(DemoAccounts.ConfigKey, json);
            b.ConfigureTestServices(s => s.AddSingleton<IInterceptor>(race));
        });

        var res = await PostgresApiFactory.PostLoginAsync(PostgresApiFactory.NewClient(host), email, "wrong-password-123456");

        Assert.True(race.Raced);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        await using var db = api.NewContext();
        var winner = Assert.Single(db.Users.AsNoTracking().Where(u => u.Email == email));
        Assert.Equal(1, winner.AccessFailedCount);
    }

    /// <summary>Just before EF inserts the user, creates the same account through another host's user manager.</summary>
    private sealed class UserInsertRace(PostgresApiFactory api, string email) : DbCommandInterceptor
    {
        public bool Raced { get; private set; }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            await RaceAsync(command);
            return result;
        }

        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken ct = default)
        {
            await RaceAsync(command);
            return result;
        }

        private async Task RaceAsync(DbCommand command)
        {
            if (Raced || !command.CommandText.Contains("INSERT INTO auth.users", StringComparison.Ordinal))
                return;
            Raced = true;
            await using var scope = api.Services.CreateAsyncScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
            var winner = new DeskUser { UserName = email, Email = email, EmailConfirmed = true, ExpiresAt = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero) };
            Assert.True((await users.CreateAsync(winner, PostgresApiFactory.Password)).Succeeded);
            Assert.True((await users.AddToRoleAsync(winner, Roles.Viewer)).Succeeded);
        }
    }

    [Fact]
    public async Task Without_the_setting_there_are_no_demo_accounts()
    {
        var res = await PostgresApiFactory.PostLoginAsync(api.NewClient(), "demo-viewer@example.com");
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("  ", 0)]
    [InlineData("not json", 0)]
    [InlineData("null", 0)]
    [InlineData("""{"email":"a@example.com"}""", 0)]
    [InlineData("""[{"email":"a@example.com","password":"p","role":"owner","expires":"2099-01-01T00:00:00Z"}]""", 0)]
    [InlineData("""[{"email":"","password":"p","role":"viewer","expires":"2099-01-01T00:00:00Z"}]""", 0)]
    [InlineData("""[{"email":"a@example.com","password":"","role":"viewer","expires":"2099-01-01T00:00:00Z"}]""", 0)]
    [InlineData("""[{"email":"a@example.com","password":"p","role":"viewer"}]""", 0)]
    [InlineData("""[{"email":"a@example.com","password":"p","role":"admin","expires":"2099-01-01T00:00:00Z"},{"email":"b@example.com","password":"p","role":"viewer","expires":"2099-01-01T00:00:00Z"}]""", 2)]
    public void Parse_keeps_only_complete_entries_and_never_throws(string? json, int expected)
    {
        Assert.Equal(expected, DemoAccounts.Parse(json, NullLogger.Instance).Count);
    }

    [Fact]
    public void Parse_is_case_insensitive_on_email()
    {
        var parsed = DemoAccounts.Parse("""[{"email":"Mixed@Example.com","password":"p","role":"viewer","expires":"2099-01-01T00:00:00Z"}]""", NullLogger.Instance);
        Assert.True(parsed.ContainsKey("mixed@example.com"));
        Assert.Equal(Roles.Viewer, parsed["mixed@example.com"].Role);
    }
}

/// <summary>The race paths of <see cref="DemoAccounts.FindOrCreateAsync"/>, made deterministic with a stub user manager.</summary>
public sealed class DemoAccountRaceTests
{
    private const string Email = "race@example.com";

    private static DemoAccounts Demo() => new(
        new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [DemoAccounts.ConfigKey] = $$"""[{"email":"{{Email}}","password":"{{PostgresApiFactory.Password}}","role":"viewer","expires":"2099-01-01T00:00:00Z"}]""",
            })
            .Build(),
        NullLogger<DemoAccounts>.Instance);

    /// <summary>A context that is never opened: the stubs don't touch the database.</summary>
    private static AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql().Options);

    [Fact]
    public async Task Losing_the_unique_index_race_returns_the_winner()
    {
        var users = new StubUsers(create: () => throw new Microsoft.EntityFrameworkCore.DbUpdateException("duplicate key"));
        var user = await Demo().FindOrCreateAsync(Email, users, Db(), TestContext.Current.CancellationToken);
        Assert.Same(users.Winner, user);
    }

    [Fact]
    public async Task Losing_identity_s_duplicate_check_returns_the_winner()
    {
        var users = new StubUsers(create: () => Microsoft.AspNetCore.Identity.IdentityResult.Failed(new Microsoft.AspNetCore.Identity.IdentityError { Code = "DuplicateUserName" }));
        var user = await Demo().FindOrCreateAsync(Email, users, Db(), TestContext.Current.CancellationToken);
        Assert.Same(users.Winner, user);
    }

    [Fact]
    public async Task An_account_that_cannot_get_its_role_is_removed_and_refused()
    {
        var users = new StubUsers(
            create: () => IdentityResult.Success,
            addToRole: () => IdentityResult.Failed(new IdentityError { Code = "ConcurrencyFailure" }));
        var user = await Demo().FindOrCreateAsync(Email, users, Db(), TestContext.Current.CancellationToken);
        Assert.Null(user);
        Assert.Equal(Email, Assert.Single(users.Deleted).Email);
    }

    [Fact]
    public async Task A_new_account_gets_its_role()
    {
        var users = new StubUsers(create: () => IdentityResult.Success);
        var user = await Demo().FindOrCreateAsync(Email, users, Db(), TestContext.Current.CancellationToken);
        Assert.Equal(Email, user!.Email);
        Assert.Equal([Roles.Viewer], users.RolesAdded);
        Assert.Empty(users.Deleted);
    }

    /// <summary>First lookup misses (the account doesn't exist yet); the create loses; the re-lookup finds the winner's row.</summary>
    private sealed class StubUsers(Func<IdentityResult> create, Func<IdentityResult>? addToRole = null)
        : UserManager<DeskUser>(new NoStore(), null!, null!, [], [], null!, null!, null!, NullLogger<UserManager<DeskUser>>.Instance)
    {
        private int _lookups;
        public DeskUser Winner { get; } = new() { Email = Email };
        public List<string> RolesAdded { get; } = [];
        public List<DeskUser> Deleted { get; } = [];

        public override Task<DeskUser?> FindByEmailAsync(string email) => Task.FromResult(_lookups++ == 0 ? null : Winner);
        public override Task<IdentityResult> CreateAsync(DeskUser user, string password) => Task.FromResult(create());

        public override Task<IdentityResult> AddToRoleAsync(DeskUser user, string role)
        {
            RolesAdded.Add(role);
            return Task.FromResult(addToRole is null ? IdentityResult.Success : addToRole());
        }

        public override Task<IdentityResult> DeleteAsync(DeskUser user)
        {
            Deleted.Add(user);
            return Task.FromResult(IdentityResult.Success);
        }
    }

    private sealed class NoStore : Microsoft.AspNetCore.Identity.IUserStore<DeskUser>
    {
        public void Dispose() { }
        public Task<string> GetUserIdAsync(DeskUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetUserNameAsync(DeskUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetUserNameAsync(DeskUser user, string? userName, CancellationToken ct) => throw new NotSupportedException();
        public Task<string?> GetNormalizedUserNameAsync(DeskUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task SetNormalizedUserNameAsync(DeskUser user, string? normalizedName, CancellationToken ct) => throw new NotSupportedException();
        public Task<Microsoft.AspNetCore.Identity.IdentityResult> CreateAsync(DeskUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<Microsoft.AspNetCore.Identity.IdentityResult> UpdateAsync(DeskUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<Microsoft.AspNetCore.Identity.IdentityResult> DeleteAsync(DeskUser user, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeskUser?> FindByIdAsync(string userId, CancellationToken ct) => throw new NotSupportedException();
        public Task<DeskUser?> FindByNameAsync(string normalizedUserName, CancellationToken ct) => throw new NotSupportedException();
    }
}
