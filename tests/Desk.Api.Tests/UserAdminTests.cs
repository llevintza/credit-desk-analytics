using System.Net;
using System.Text.RegularExpressions;
using Desk.Data.Auth;
using Desk.UserAdmin;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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

    private Task<Run> RunAsync(params string[] args) => RunAsync(null, args);

    private async Task<Run> RunAsync(Action<IServiceCollection>? configure, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var logs = new CapturingLoggerProvider();
        using var loggers = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:App"] = api.ConnectionString })
            .Build();
        var app = new UserAdminApp(stdout, stderr, loggers, api.Time) { ConfigureServices = configure };
        var exit = await app.RunAsync(args, config, Ct);
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

    private async Task<(string? Stamp, int[] Grants, string?[] GrantedBy)> GrantStateAsync(string email)
    {
        await using var db = api.NewContext();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct);
        var grants = await db.PortfolioGrants.AsNoTracking().Where(g => g.UserId == user.Id).OrderBy(g => g.PortfolioId).ToListAsync(Ct);
        return (user.SecurityStamp, [.. grants.Select(g => g.PortfolioId)], [.. grants.Select(g => g.GrantedBy)]);
    }

    [Fact]
    public async Task Grant_and_revoke_change_the_grants_without_rotating_the_security_stamp()
    {
        // ADR-0021: a grant or revoke applies at the next token refresh; it must never sign the user out.
        var email = Email();
        Assert.Equal(0, (await RunAsync("add", "--email", email, "--role", "viewer", "--expires", "2027-01-01")).Exit);
        var (stamp, _, _) = await GrantStateAsync(email);

        var granted = await RunAsync("grant", "--email", email, "--portfolio", "3,1");
        Assert.Equal(0, granted.Exit);
        Assert.Contains("Granted portfolios 1, 3", granted.Out);
        var afterGrant = await GrantStateAsync(email);
        Assert.Equal(stamp, afterGrant.Stamp);
        Assert.Equal([1, 3], afterGrant.Grants);
        Assert.All(afterGrant.GrantedBy, by => Assert.Equal("Desk.UserAdmin", by));

        // Granting again is a no-op for the ids already there.
        var again = await RunAsync("grant", "--email", email, "--portfolio", "1,2");
        Assert.Equal(0, again.Exit);
        Assert.Contains("Granted portfolios 2", again.Out);
        Assert.Contains("already had 1", again.Out);
        Assert.Equal(new[] { 1, 2, 3 }, (await GrantStateAsync(email)).Grants);

        var nothingNew = await RunAsync("grant", "--email", email, "--portfolio", "2");
        Assert.Equal(0, nothingNew.Exit);
        Assert.Contains("No new grants", nothingNew.Out);

        var revoked = await RunAsync("revoke", "--email", email, "--portfolio", "1,3,9");
        Assert.Equal(0, revoked.Exit);
        Assert.Contains("Revoked portfolios 1, 3", revoked.Out);
        var afterRevoke = await GrantStateAsync(email);
        Assert.Equal(stamp, afterRevoke.Stamp);
        Assert.Equal([2], afterRevoke.Grants);

        var nothingToRevoke = await RunAsync("revoke", "--email", email, "--portfolio", "7");
        Assert.Equal(0, nothingToRevoke.Exit);
        Assert.Contains("had none of those grants", nothingToRevoke.Out);
        Assert.Equal(new[] { 2 }, (await GrantStateAsync(email)).Grants);
    }

    [Fact]
    public async Task List_shows_each_account_s_grants()
    {
        var viewer = Email();
        var none = Email();
        var admin = Email();
        await RunAsync("add", "--email", viewer, "--role", "viewer", "--expires", "2027-03-31");
        await RunAsync("add", "--email", none, "--role", "viewer", "--expires", "2027-03-31");
        await RunAsync("add", "--email", admin, "--role", "admin", "--expires", "2027-03-31");
        await RunAsync("grant", "--email", viewer, "--portfolio", "4,2");

        var run = await RunAsync("list");
        Assert.Equal(0, run.Exit);
        Assert.Contains("GRANTS", run.Out);
        Assert.Matches($@"{Regex.Escape(viewer)}\s+viewer\s+2027-03-31\s+active\s+2,4\r?$", run.Out.ReplaceLineEndings("\n").Split('\n').Single(l => l.Contains(viewer)));
        Assert.Matches($@"{Regex.Escape(none)}\s+viewer\s+2027-03-31\s+active\s+none$", run.Out.ReplaceLineEndings("\n").Split('\n').Single(l => l.Contains(none)));
        Assert.Matches($@"{Regex.Escape(admin)}\s+admin\s+2027-03-31\s+active\s+all \(admin\)$", run.Out.ReplaceLineEndings("\n").Split('\n').Single(l => l.Contains(admin)));
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("revoke")]
    public async Task Grant_or_revoke_for_an_unknown_email_is_exit_1(string command)
    {
        var run = await RunAsync(command, "--email", "missing@example.com", "--portfolio", "1");
        Assert.Equal(1, run.Exit);
        Assert.Contains("No account", run.Err);
    }

    [Fact]
    public async Task Reset_issues_a_new_password_once_and_clears_the_lockout()
    {
        var email = Email();
        var added = await RunAsync("add", "--email", email, "--role", "viewer", "--expires", "2027-01-01");
        var oldPassword = PasswordLine().Match(added.Out).Groups[1].Value;
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Email == email)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, api.Time.GetUtcNow().AddMinutes(10)).SetProperty(u => u.AccessFailedCount, 5), Ct);
        // Locked on the fixture clock (#261), even for the right password.
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(api.NewClient(), email, oldPassword)).StatusCode);
        string? oldStamp;
        await using (var db = api.NewContext())
            oldStamp = (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct)).SecurityStamp;

        var run = await RunAsync("reset", "--email", email);
        Assert.Equal(0, run.Exit);
        var newPassword = Assert.Single(PasswordLine().Matches(run.Out)).Groups[1].Value;
        Assert.NotEqual(oldPassword, newPassword);
        Assert.DoesNotContain(newPassword, run.Logs);
        await using (var db = api.NewContext())
        {
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct);
            Assert.NotEqual(oldStamp, user.SecurityStamp); // live sessions end (README §7.1)
            Assert.Null(user.LockoutEnd);
            Assert.Equal(0, user.AccessFailedCount);
        }

        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(api.NewClient(), email, oldPassword)).StatusCode);
        await PostgresApiFactory.LoginAsync(api.NewClient(), email, newPassword);
    }

    [Fact]
    public async Task A_failed_reset_leaves_the_old_password_lockout_and_failure_count_untouched()
    {
        var email = Email();
        var added = await RunAsync("add", "--email", email, "--role", "viewer", "--expires", "2027-01-01");
        var oldPassword = PasswordLine().Match(added.Out).Groups[1].Value;
        var lockoutEnd = DateTimeOffset.UtcNow.AddMinutes(10);
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Email == email)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, lockoutEnd).SetProperty(u => u.AccessFailedCount, 3), Ct);
        DeskUser before;
        await using (var db = api.NewContext())
            before = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct);

        var run = await RunAsync(s => s
            .AddScoped<IPasswordValidator<DeskUser>, RejectingPasswordValidator>()
            .AddScoped<IPasswordValidator<DeskUser>, SecondRejectingPasswordValidator>(), "reset", "--email", email);

        Assert.Equal(3, run.Exit);
        Assert.Contains(RejectingPasswordValidator.Reason, run.Err); // every validator's reason is reported
        Assert.Contains(SecondRejectingPasswordValidator.Reason, run.Err);
        Assert.DoesNotMatch(PasswordLine(), run.Out);
        await using (var db = api.NewContext())
        {
            var after = await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct);
            Assert.NotNull(after.PasswordHash);
            Assert.Equal(before.PasswordHash, after.PasswordHash);
            Assert.Equal(before.SecurityStamp, after.SecurityStamp);
            Assert.Equal(before.LockoutEnd, after.LockoutEnd);
            Assert.Equal(3, after.AccessFailedCount);
        }

        // The old password still verifies; the lockout set above still holds until it is cleared.
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Email == email)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, (DateTimeOffset?)null).SetProperty(u => u.AccessFailedCount, 0), Ct);
        await PostgresApiFactory.LoginAsync(api.NewClient(), email, oldPassword);
    }

    [Fact]
    public async Task A_validator_failure_without_errors_still_fails_the_reset()
    {
        var email = Email();
        Assert.Equal(0, (await RunAsync("add", "--email", email, "--role", "viewer", "--expires", "2027-01-01")).Exit);
        string? hash;
        await using (var db = api.NewContext())
            hash = (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct)).PasswordHash;

        var run = await RunAsync(s => s.AddScoped<IPasswordValidator<DeskUser>, SilentlyRejectingPasswordValidator>(), "reset", "--email", email);

        Assert.Equal(3, run.Exit);
        Assert.DoesNotMatch(PasswordLine(), run.Out);
        await using (var db = api.NewContext())
            Assert.Equal(hash, (await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct)).PasswordHash);
    }

    private sealed class SilentlyRejectingPasswordValidator : IPasswordValidator<DeskUser>
    {
        public Task<IdentityResult> ValidateAsync(UserManager<DeskUser> manager, DeskUser user, string? password) =>
            Task.FromResult(IdentityResult.Failed());
    }

    private sealed class RejectingPasswordValidator : IPasswordValidator<DeskUser>
    {
        public const string Reason = "Rejected by the test validator.";

        public Task<IdentityResult> ValidateAsync(UserManager<DeskUser> manager, DeskUser user, string? password) =>
            Task.FromResult(IdentityResult.Failed(new IdentityError { Description = Reason }));
    }

    private sealed class SecondRejectingPasswordValidator : IPasswordValidator<DeskUser>
    {
        public const string Reason = "Also rejected by a second test validator.";

        public Task<IdentityResult> ValidateAsync(UserManager<DeskUser> manager, DeskUser user, string? password) =>
            Task.FromResult(IdentityResult.Failed(new IdentityError { Description = Reason }));
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
    [InlineData("grant", "--email", "a@example.com")]
    [InlineData("grant", "--email", "a@example.com", "--portfolio", "")]
    [InlineData("grant", "--email", "a@example.com", "--portfolio", "abc")]
    [InlineData("grant", "--email", "a@example.com", "--portfolio", "0")]
    [InlineData("revoke", "--email", "a@example.com", "--portfolio", "1,-2")]
    [InlineData("revoke", "--portfolio", "1")]
    [InlineData("add", "--email", "a@example.com", "--role", "viewer", "--expires", "2027-01-01", "--portfolio", "1")]
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
