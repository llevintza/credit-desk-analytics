using System.Net;
using Desk.Api.Auth;
using Desk.Data.Auth;
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
