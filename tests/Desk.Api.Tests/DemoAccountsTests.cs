using System.Net;
using Desk.Api.Auth;
using Desk.Data.Auth;
using Microsoft.Extensions.Configuration;
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

    [Fact]
    public async Task Losing_the_unique_index_race_returns_the_winner()
    {
        var users = new StubUsers(create: () => throw new Microsoft.EntityFrameworkCore.DbUpdateException("duplicate key"));
        var user = await Demo().FindOrCreateAsync(Email, users, TestContext.Current.CancellationToken);
        Assert.Same(users.Winner, user);
    }

    [Fact]
    public async Task Losing_identity_s_duplicate_check_returns_the_winner()
    {
        var users = new StubUsers(create: () => Microsoft.AspNetCore.Identity.IdentityResult.Failed(new Microsoft.AspNetCore.Identity.IdentityError { Code = "DuplicateUserName" }));
        var user = await Demo().FindOrCreateAsync(Email, users, TestContext.Current.CancellationToken);
        Assert.Same(users.Winner, user);
    }

    /// <summary>First lookup misses (the account doesn't exist yet); the create loses; the re-lookup finds the winner's row.</summary>
    private sealed class StubUsers(Func<Microsoft.AspNetCore.Identity.IdentityResult> create)
        : Microsoft.AspNetCore.Identity.UserManager<DeskUser>(new NoStore(), null!, null!, [], [], null!, null!, null!, NullLogger<Microsoft.AspNetCore.Identity.UserManager<DeskUser>>.Instance)
    {
        private int _lookups;
        public DeskUser Winner { get; } = new() { Email = Email };

        public override Task<DeskUser?> FindByEmailAsync(string email) => Task.FromResult(_lookups++ == 0 ? null : Winner);
        public override Task<Microsoft.AspNetCore.Identity.IdentityResult> CreateAsync(DeskUser user, string password) => Task.FromResult(create());
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
