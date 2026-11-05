using System.Net;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json;
using Desk.Api.Auth;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Mcp.Tests;

/// <summary>
/// <c>desk-mcp login</c>, the token file, on-demand refresh, refresh-token reuse (#353) and logout, against the test API
/// (ADR-0031 §2, D16). Hosts that move time have their own fake clock.
/// </summary>
[Collection(DeskApiCollection.Name)]
public sealed class SignInTests(DeskApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);

    private static async Task<HttpStatusCode> RefreshStatusAsync(Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<global::Program> host, string token)
    {
        using var http = host.CreateClient();
        using var res = await http.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(token), Ct);
        return res.StatusCode;
    }

    [Fact]
    [UnsupportedOSPlatform("windows")]
    public async Task Login_stores_only_the_origin_and_refresh_token_in_a_0600_file_and_prints_no_secret()
    {
        var host = api.Host();
        using var home = new ConfigHome();
        var email = await DeskApiFixture.CreateUserAsync(host, DateTimeOffset.UtcNow);
        var stderr = new StringWriter();
        Assert.Equal(0, await McpSession.LoginAsync(host, home, email, stderr));

        var store = home.Store();
        Assert.StartsWith(home.Path, store.FilePath);
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(store.FilePath));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(store.Directory));
        Assert.Equal([store.FilePath], Directory.GetFiles(store.Directory)); // no temp file left behind

        var text = await File.ReadAllTextAsync(store.FilePath, Ct);
        var saved = JsonDocument.Parse(text).RootElement;
        Assert.Equal(["origin", "refreshToken"], saved.EnumerateObject().Select(p => p.Name));
        Assert.Equal(McpSession.Origin, saved.GetProperty("origin").GetString());
        Assert.DoesNotContain(email, text);
        Assert.DoesNotContain(DeskApiFixture.Password, text);

        var output = stderr.ToString();
        Assert.Contains("Signed in to http://localhost", output);
        Assert.DoesNotContain(saved.GetProperty("refreshToken").GetString()!, output);
        Assert.DoesNotContain(email, output);
        Assert.DoesNotContain(DeskApiFixture.Password, output);
    }

    [Fact]
    public async Task Serve_never_writes_a_token_email_or_password_to_stderr()
    {
        var (host, clock) = OwnClockHost();
        using var home = new ConfigHome();
        var email = await DeskApiFixture.CreateUserAsync(host, Start);
        Assert.Equal(0, await McpSession.LoginAsync(host, home, email, TextWriter.Null));
        var tokens = new List<string> { home.Store().Read()!.RefreshToken };
        await using (var session = await McpSession.StartAsync(host, home))
        {
            await session.JsonAsync("list_portfolios");
            tokens.Add(home.Store().Read()!.RefreshToken);
            clock.Advance(AuthSetup.AccessTokenLifetime + TimeSpan.FromMinutes(1));
            await session.JsonAsync("get_as_of_dates"); // a 401, then a refresh: both logged
            tokens.Add(home.Store().Read()!.RefreshToken);

            var output = session.Stderr.ToString();
            Assert.Contains("refreshing once", output);
            Assert.Contains("Refreshed the access token", output);
            Assert.All(tokens, t => Assert.DoesNotContain(t, output));
            Assert.DoesNotContain(email, output);
            Assert.DoesNotContain(DeskApiFixture.Password, output);
            Assert.DoesNotContain("Bearer", output);
        }
        Assert.Equal(3, tokens.Distinct().Count());
    }

    [Fact]
    public async Task A_refresh_after_access_token_expiry_works_and_rotates_the_saved_token()
    {
        var (host, clock) = OwnClockHost();
        using var home = new ConfigHome();
        Assert.Equal(0, await McpSession.LoginAsync(host, home, await DeskApiFixture.CreateUserAsync(host, Start), TextWriter.Null));
        await using var session = await McpSession.StartAsync(host, home);
        await session.JsonAsync("list_portfolios");
        var before = home.Store().Read()!.RefreshToken;

        clock.Advance(AuthSetup.AccessTokenLifetime + TimeSpan.FromSeconds(1));
        var rows = (await session.JsonAsync("list_portfolios")).GetProperty("rows");
        Assert.True(rows.GetArrayLength() > 0);
        var after = home.Store().Read()!.RefreshToken;
        Assert.NotEqual(before, after);
        Assert.Equal(HttpStatusCode.OK, await RefreshStatusAsync(host, after));
    }

    [Fact]
    public async Task A_reused_refresh_token_ends_the_sign_in_and_the_tool_reports_re_login()
    {
        var (host, clock) = OwnClockHost();
        using var home = new ConfigHome();
        Assert.Equal(0, await McpSession.LoginAsync(host, home, await DeskApiFixture.CreateUserAsync(host, Start), TextWriter.Null));
        var store = home.Store();
        var first = store.Read()!.RefreshToken;
        await using var session = await McpSession.StartAsync(host, home);
        await session.JsonAsync("list_portfolios"); // swaps the first token
        var second = store.Read()!.RefreshToken;

        // The swapped token comes back (a copy, another process), past the grace window and the access token's life.
        store.Write(first);
        clock.Advance(AuthSetup.AccessTokenLifetime + SessionStore.DefaultGrace);
        var error = await session.ErrorAsync("list_portfolios");
        Assert.Contains("Sign in again with `desk-mcp login --api http://localhost`", error);
        Assert.Null(store.Read());
        Assert.DoesNotContain(first, error);

        // #353: the reuse ended the session, so the token that replaced it is refused as well.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(host, second));
        Assert.Contains("Not signed in", await session.ErrorAsync("get_as_of_dates"));
    }

    [Fact]
    public async Task Past_the_24_h_cap_the_refresh_is_refused_and_the_tool_reports_re_login()
    {
        var (host, clock) = OwnClockHost();
        using var home = new ConfigHome();
        Assert.Equal(0, await McpSession.LoginAsync(host, home, await DeskApiFixture.CreateUserAsync(host, Start), TextWriter.Null));
        await using var session = await McpSession.StartAsync(host, home);
        await session.JsonAsync("list_columns");
        clock.Advance(AuthSetup.AbsoluteExpiry);
        Assert.Contains("Sign in again", await session.ErrorAsync("list_columns"));
    }

    [Fact]
    public async Task Logout_ends_the_session_on_the_api_and_deletes_the_token_file()
    {
        var host = api.Host();
        using var home = new ConfigHome();
        Assert.Equal(0, await McpSession.LoginAsync(host, home, await DeskApiFixture.CreateUserAsync(host, DateTimeOffset.UtcNow), TextWriter.Null));
        var token = home.Store().Read()!.RefreshToken;
        var stderr = new StringWriter();
        var app = McpSession.App(host, home, stderr);
        Assert.Equal(0, await app.RunAsync(["logout", "--api", McpSession.Origin], Stream.Null, Stream.Null, Ct));
        Assert.False(File.Exists(home.Store().FilePath));
        Assert.Contains("Signed out of http://localhost", stderr.ToString());
        // The exchange in logout swapped the saved token, and logout ended the session: the old token is now a dead replay.
        Assert.Equal(HttpStatusCode.Unauthorized, await RefreshStatusAsync(host, token));

        Assert.Equal(0, await app.RunAsync(["logout", "--api", McpSession.Origin], Stream.Null, Stream.Null, Ct));
        Assert.Contains("Not signed in to http://localhost", stderr.ToString());
    }

    [Fact]
    public async Task Logout_with_a_dead_token_still_deletes_the_file()
    {
        var host = api.Host();
        using var home = new ConfigHome();
        home.Store().Write("not-a-real-token");
        Assert.Equal(0, await McpSession.App(host, home, TextWriter.Null).RunAsync(["logout", "--api", McpSession.Origin], Stream.Null, Stream.Null, Ct));
        Assert.Null(home.Store().Read());
    }

    [Fact]
    public async Task A_wrong_password_exits_1_and_saves_nothing()
    {
        var host = api.Host();
        using var home = new ConfigHome();
        var stderr = new StringWriter();
        var email = await DeskApiFixture.CreateUserAsync(host, DateTimeOffset.UtcNow);
        Assert.Equal(1, await McpSession.LoginAsync(host, home, email, stderr, password: "wrong-" + Guid.NewGuid()));
        Assert.Contains("Login failed", stderr.ToString());
        Assert.DoesNotContain(email, stderr.ToString());
        Assert.False(Directory.Exists(home.Store().Directory) && Directory.GetFiles(home.Store().Directory, "*.json").Length > 0);
    }

    [Fact]
    public async Task Login_without_a_terminal_refuses_to_read_credentials()
    {
        using var home = new ConfigHome();
        var stderr = new StringWriter();
        var app = McpSession.App(api.Host(), home, stderr, new FakePrompt("a", "b", interactive: false));
        Assert.Equal(2, await app.RunAsync(["login", "--api", McpSession.Origin], Stream.Null, Stream.Null, Ct));
        Assert.Contains("needs a terminal", stderr.ToString());
    }

    private (Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<global::Program> Host, FakeTimeProvider Clock) OwnClockHost()
    {
        var clock = new FakeTimeProvider(Start);
        return (api.Host(time: clock), clock);
    }
}
