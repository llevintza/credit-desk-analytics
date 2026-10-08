using System.Net;
using System.Net.Http.Json;
using Desk.Api.Auth;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Desk.Api.Tests;

/// <summary>
/// R105-F2 (#118): every login attempt runs exactly one PBKDF2 verification, whatever the account's state, so the
/// response time doesn't reveal whether an email is a real account. Counted through the app's own hasher.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class LoginTimingTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public enum LoginPath
    {
        UnknownEmail,
        MissingEmail,
        WrongPassword,
        EmptyPassword,
        MissingPassword,
        Locked,
        LockedWithTheRightPassword,
        Disabled,
        Expired,
        NoStoredPassword,
        TriggersLockout,
        Success,
    }

    [Theory]
    [InlineData(LoginPath.UnknownEmail)]
    [InlineData(LoginPath.MissingEmail)]
    [InlineData(LoginPath.WrongPassword)]
    [InlineData(LoginPath.EmptyPassword)]
    [InlineData(LoginPath.MissingPassword)]
    [InlineData(LoginPath.Locked)]
    [InlineData(LoginPath.LockedWithTheRightPassword)]
    [InlineData(LoginPath.Disabled)]
    [InlineData(LoginPath.Expired)]
    [InlineData(LoginPath.NoStoredPassword)]
    [InlineData(LoginPath.TriggersLockout)]
    [InlineData(LoginPath.Success)]
    public async Task Every_login_runs_exactly_one_password_hash(LoginPath path)
    {
        var hasher = new CountingHasher();
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IPasswordHasher<DeskUser>>();
            s.AddSingleton<IPasswordHasher<DeskUser>>(hasher);
        }));
        var (email, password) = await ArrangeAsync(path);

        hasher.Reset();
        var res = await PostgresApiFactory.NewClient(host).PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Ct);

        Assert.Equal(path == LoginPath.Success ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Equal(1, hasher.Verifications);
        Assert.Equal(0, hasher.Hashes);
        if (path == LoginPath.TriggersLockout)
        {
            await using var db = api.NewContext();
            Assert.NotNull((await db.Users.AsNoTracking().SingleAsync(u => u.Email == email, Ct)).LockoutEnd);
        }
    }

    private async Task<(string? Email, string? Password)> ArrangeAsync(LoginPath path)
    {
        switch (path)
        {
            case LoginPath.UnknownEmail:
                return ($"nobody-{Guid.NewGuid():N}@example.com", PostgresApiFactory.Password);
            case LoginPath.MissingEmail:
                return (null, PostgresApiFactory.Password);
            case LoginPath.NoStoredPassword:
                return (await CreateWithoutPasswordAsync(), PostgresApiFactory.Password);
            case LoginPath.Expired:
                return ((await api.CreateUserAsync(expiresAt: api.Time.GetUtcNow().AddMinutes(-1))).Email, PostgresApiFactory.Password);
        }

        var user = await api.CreateUserAsync();
        await using var db = api.NewContext();
        var row = db.Users.Where(u => u.Id == user.Id);
        switch (path)
        {
            case LoginPath.WrongPassword:
                return (user.Email, "wrong-password-123456");
            case LoginPath.EmptyPassword:
                return (user.Email, "");
            case LoginPath.MissingPassword:
                return (user.Email, null);
            case LoginPath.Locked:
            case LoginPath.LockedWithTheRightPassword:
                // Far past both the fake clock and the wall clock, whichever Identity reads.
                await row.ExecuteUpdateAsync(s => s.SetProperty(u => u.LockoutEnd, DateTimeOffset.UtcNow.AddYears(10)), Ct);
                return (user.Email, path == LoginPath.Locked ? "wrong-password-123456" : PostgresApiFactory.Password);
            case LoginPath.Disabled:
                await row.ExecuteUpdateAsync(s => s.SetProperty(u => u.IsDisabled, true), Ct);
                return (user.Email, PostgresApiFactory.Password);
            case LoginPath.TriggersLockout:
                await row.ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, IdentityPolicy.MaxFailedAttempts - 1), Ct);
                return (user.Email, "wrong-password-123456");
            default:
                return (user.Email, PostgresApiFactory.Password);
        }
    }

    private async Task<string> CreateWithoutPasswordAsync()
    {
        await using var scope = api.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<DeskUser>>();
        var email = $"nopassword-{Guid.NewGuid():N}@example.com";
        var created = await users.CreateAsync(new DeskUser { UserName = email, Email = email, EmailConfirmed = true, ExpiresAt = api.Time.GetUtcNow().AddDays(30) });
        Assert.True(created.Succeeded);
        return email;
    }

    [Fact]
    public async Task Without_lockout_on_failure_a_wrong_password_is_not_counted()
    {
        var user = await api.CreateUserAsync();
        await using var scope = api.Services.CreateAsyncScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<DeskUser>>();
        var tracked = await signIn.UserManager.FindByIdAsync(user.Id.ToString());

        var result = await signIn.CheckPasswordSignInAsync(tracked!, "wrong-password-123456", lockoutOnFailure: false);

        Assert.Equal(SignInResult.Failed, result);
        Assert.Equal(0, await signIn.UserManager.GetAccessFailedCountAsync(tracked!));
    }

    [Fact]
    public async Task A_correct_password_clears_earlier_failures()
    {
        var user = await api.CreateUserAsync();
        var client = api.NewClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await PostgresApiFactory.PostLoginAsync(client, user.Email!, "wrong-password-123456")).StatusCode);
        await PostgresApiFactory.LoginAsync(client, user.Email!);

        await using var db = api.NewContext();
        Assert.Equal(0, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct)).AccessFailedCount);
    }

    [Fact]
    public void The_decoy_costs_what_a_real_hash_costs_and_never_matches()
    {
        var real = new PasswordHasher<DeskUser>();
        var options = new PasswordHasherOptions();
        var decoy = TimingGuard.DecoyHash(options.IterationCount);

        // Format marker, PRF, iteration count and salt size: the PBKDF2 parameters a verification runs with.
        var realHeader = Convert.FromBase64String(real.HashPassword(new DeskUser(), PostgresApiFactory.Password))[..13];
        Assert.Equal(realHeader, Convert.FromBase64String(decoy)[..13]);
        Assert.Equal(Convert.FromBase64String(real.HashPassword(new DeskUser(), PostgresApiFactory.Password)).Length, Convert.FromBase64String(decoy).Length);
        Assert.Equal(PasswordVerificationResult.Failed, real.VerifyHashedPassword(new DeskUser(), decoy, PostgresApiFactory.Password));
        Assert.Equal(PasswordVerificationResult.Failed, real.VerifyHashedPassword(new DeskUser(), decoy, ""));
    }

    /// <summary>The app's hasher, counting each PBKDF2 run.</summary>
    private sealed class CountingHasher : IPasswordHasher<DeskUser>
    {
        private readonly PasswordHasher<DeskUser> _inner = new();
        private int _verifications;
        private int _hashes;

        public int Verifications => Volatile.Read(ref _verifications);
        public int Hashes => Volatile.Read(ref _hashes);

        public void Reset()
        {
            Interlocked.Exchange(ref _verifications, 0);
            Interlocked.Exchange(ref _hashes, 0);
        }

        public string HashPassword(DeskUser user, string password)
        {
            Interlocked.Increment(ref _hashes);
            return _inner.HashPassword(user, password);
        }

        public PasswordVerificationResult VerifyHashedPassword(DeskUser user, string hashedPassword, string providedPassword)
        {
            Interlocked.Increment(ref _verifications);
            return _inner.VerifyHashedPassword(user, hashedPassword, providedPassword);
        }
    }
}
