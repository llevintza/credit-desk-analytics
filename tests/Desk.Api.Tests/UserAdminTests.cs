using System.Net;
using System.Text.RegularExpressions;
using Desk.Data.Auth;
using Desk.UserAdmin;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Desk.Api.Tests;

/// <summary>README §7.1 UserAdmin CLI against the shared Testcontainers database (#37).</summary>
[Collection(ApiCollection.Name)]
public sealed partial class UserAdminTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"Password \(shown once, share it out of band\): (\S+)")]
    private static partial Regex PasswordLine();

    private sealed record Run(int Exit, string Out, string Err, string Logs);

    private async Task<Run> RunAsync(params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:App"] = api.ConnectionString })
            .Build();
        var exit = await new UserAdminApp(stdout, stderr, loggers, api.Time).RunAsync(args, config, Ct);
        return new Run(exit, stdout.ToString(), stderr.ToString(), logs.Text);
    }

    private static string Email() => $"cli-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task Add_prints_a_strong_password_once_stores_only_the_hash_and_never_logs_it()
    {
        var email = Email();
        var run = await RunAsync("add", "--email", email, "--role", "viewer", "--expires", "2026-11-30");

        Assert.Equal(0, run.Exit);
        var matches = PasswordLine().Matches(run.Out);
        var password = Assert.Single(matches).Groups[1].Value;
        Assert.Equal(PasswordGenerator.DefaultLength, password.Length);
        Assert.Equal(1, Regex.Count(run.Out, Regex.Escape(password)));
        Assert.DoesNotContain(password, run.Err);
        Assert.NotEmpty(run.Logs); // EF and Identity did log; the password is just never in it
        Assert.DoesNotContain(password, run.Logs);

        await using var db = api.NewContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct);
        Assert.NotEqual(password, user.PasswordHash);
        Assert.DoesNotContain(password, user.PasswordHash!);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), user.ExpiresAt);

        // The printed password works against the API.
        await PostgresApiFactory.LoginAsync(api.NewClient(), email, password);
    }

    [Fact]
    public async Task Add_refuses_an_existing_email_and_a_past_expiry()
    {
        var email = Email();
        Assert.Equal(0, (await RunAsync("add", "--email", email, "--role", "admin", "--expires", "2027-01-01")).Exit);

        var again = await RunAsync("add", "--email", email, "--role", "admin", "--expires", "2027-01-01");
        Assert.Equal(1, again.Exit);
        Assert.Contains("already exists", again.Err);

        var past = await RunAsync("add", "--email", Email(), "--role", "viewer", "--expires", "2020-01-01");
        Assert.Equal(1, past.Exit);
        Assert.Contains("in the past", past.Err);
    }

    [Fact]
    public async Task List_shows_role_expiry_and_status()
    {
        var active = Email();
        var disabled = Email();
        await RunAsync("add", "--email", active, "--role", "admin", "--expires", "2027-03-31");
        await RunAsync("add", "--email", disabled, "--role", "viewer", "--expires", "2027-03-31");
        await RunAsync("disable", "--email", disabled);

        var run = await RunAsync("list");
        Assert.Equal(0, run.Exit);
        Assert.Matches($@"{Regex.Escape(active)}\s+admin\s+2027-03-31\s+active", run.Out);
        Assert.Matches($@"{Regex.Escape(disabled)}\s+viewer\s+2027-03-31\s+disabled", run.Out);
        Assert.Contains("account(s).", run.Out);
    }

    [Fact]
    public async Task List_reports_expired_and_locked_accounts()
    {
        var expired = await api.CreateUserAsync(expiresAt: api.Time.GetUtcNow().AddDays(-2));
        var locked = await api.CreateUserAsync();
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Id == locked.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, api.Time.GetUtcNow().AddMinutes(10)), Ct);

        var run = await RunAsync("list");
        Assert.Matches($@"{Regex.Escape(expired.Email!)}.*expired", run.Out);
        Assert.Matches($@"{Regex.Escape(locked.Email!)}.*locked", run.Out);
    }

    [Fact]
    public async Task Disable_blocks_login()
    {
        var email = Email();
        var added = await RunAsync("add", "--email", email, "--role", "viewer", "--expires", "2027-01-01");
        var password = PasswordLine().Match(added.Out).Groups[1].Value;

        var run = await RunAsync("disable", "--email", email);
        Assert.Equal(0, run.Exit);
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(api.NewClient(), email, password)).StatusCode);
    }

    [Fact]
    public async Task Reset_issues_a_new_password_once_and_clears_the_lockout()
    {
        var email = Email();
        var added = await RunAsync("add", "--email", email, "--role", "viewer", "--expires", "2027-01-01");
        var oldPassword = PasswordLine().Match(added.Out).Groups[1].Value;
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Email == email)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddMinutes(10)).SetProperty(u => u.AccessFailedCount, 5), Ct);

        var run = await RunAsync("reset", "--email", email);
        Assert.Equal(0, run.Exit);
        var newPassword = Assert.Single(PasswordLine().Matches(run.Out)).Groups[1].Value;
        Assert.NotEqual(oldPassword, newPassword);
        Assert.DoesNotContain(newPassword, run.Logs);

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(api.NewClient(), email, oldPassword)).StatusCode);
        await PostgresApiFactory.LoginAsync(api.NewClient(), email, newPassword);
    }

    [Theory]
    [InlineData("disable")]
    [InlineData("reset")]
    public async Task Unknown_email_is_exit_1(string command)
    {
        var run = await RunAsync(command, "--email", "missing@example.com");
        Assert.Equal(1, run.Exit);
        Assert.Contains("No account", run.Err);
    }

    [Theory]
    [InlineData()]
    [InlineData("frobnicate")]
    [InlineData("add", "--email")]
    [InlineData("add", "--email", "a@example.com", "--role", "owner", "--expires", "2027-01-01")]
    [InlineData("add", "--email", "a@example.com", "--role", "viewer")]
    [InlineData("add", "--email", "a@example.com", "--role", "viewer", "--expires", "next week")]
    [InlineData("add", "--role", "viewer", "--expires", "2027-01-01")]
    [InlineData("list", "--verbose", "yes")]
    public async Task Bad_arguments_print_usage_and_exit_1(params string[] args)
    {
        var run = await RunAsync(args);
        Assert.Equal(1, run.Exit);
        Assert.Contains("Usage:", run.Err);
    }

    [Fact]
    public async Task An_email_identity_rejects_is_exit_1_with_its_reason()
    {
        var run = await RunAsync("add", "--email", "not-an-email", "--role", "viewer", "--expires", "2027-01-01");
        Assert.Equal(1, run.Exit);
        Assert.Contains("invalid", run.Err, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(PasswordLine(), run.Out);
    }

    [Fact]
    public void A_failed_identity_step_throws_with_its_reasons()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => UserAdminApp.Check(
            IdentityResult.Failed(new IdentityError { Description = "first" }, new IdentityError { Description = "second" })));
        Assert.Equal("first second", ex.Message);
        UserAdminApp.Check(IdentityResult.Success);
    }

    [Fact]
    public void The_console_entry_point_wires_stderr_and_exit_codes()
    {
        var main = typeof(UserAdminApp).Assembly.EntryPoint!;
        var err = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            // The compiler's synchronous <Main> wrapper is the entry point; it blocks on the async Main.
            Assert.Equal(1, (int)main.Invoke(null, [new[] { "frobnicate" }])!);
        }
        finally
        {
            Console.SetError(err);
        }
        Assert.Contains("Unknown command", captured.ToString());
    }

    [Fact]
    public async Task Missing_connection_string_is_exit_1()
    {
        var stderr = new StringWriter();
        var exit = await new UserAdminApp(new StringWriter(), stderr).RunAsync(["list"], new ConfigurationBuilder().Build(), Ct);
        Assert.Equal(1, exit);
        Assert.Contains("ConnectionStrings__App", stderr.ToString());
    }

    [Fact]
    public async Task Unreachable_database_is_exit_3_and_cancel_is_130()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["DATABASE_URL"] = "Host=127.0.0.1;Port=1;Database=none;Username=none;Timeout=1" })
            .Build();
        var stderr = new StringWriter();
        Assert.Equal(3, await new UserAdminApp(new StringWriter(), stderr).RunAsync(["list"], config, Ct));
        Assert.StartsWith("ERROR:", stderr.ToString());

        var cancelled = new StringWriter();
        Assert.Equal(130, await new UserAdminApp(new StringWriter(), cancelled).RunAsync(["disable", "--email", "a@example.com"], config, new CancellationToken(true)));
        Assert.Contains("cancelled", cancelled.ToString());
    }

    [Fact]
    public void Generated_passwords_meet_the_policy_and_differ()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var p = PasswordGenerator.Generate();
            Assert.Equal(PasswordGenerator.DefaultLength, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
            Assert.True(seen.Add(p));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => PasswordGenerator.Generate(IdentityPolicy.MinPasswordLength - 1));
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _lines = new();

        public string Text => string.Join('\n', _lines);

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner._lines.Enqueue($"{formatter(state, exception)} {exception}");
        }
    }
}
