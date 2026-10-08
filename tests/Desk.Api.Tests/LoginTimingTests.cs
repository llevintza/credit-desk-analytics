using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using Desk.Api.Auth;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>
/// R105-F2 (#118): every login attempt runs exactly one PBKDF2 verification, whatever the account's state. Counted
/// through the app's own hasher. #230: the DB work still differs per state, so every failed login also answers after
/// the same floor; that runs on a fake clock that records each wait.
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
    public async Task Every_failed_login_answers_after_the_floor(LoginPath path)
    {
        var clock = new FloorClock(api.Time.GetUtcNow());
        await using var host = WithFloor(clock);
        var (email, password) = await ArrangeAsync(path);

        var pending = PostgresApiFactory.NewClient(host).PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password), Ct);

        var wait = Assert.Single(await clock.FloorWaitsAsync(1));
        Assert.InRange(wait, LoginFloorOptions.Default.Floor, LoginFloorOptions.Default.Floor + LoginFloorOptions.Default.MaxJitter);
        clock.Advance(wait - TimeSpan.FromTicks(1));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromTicks(1));
        var res = await pending.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains(AuthEndpoints.LoginFailedDetail, await res.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_successful_login_is_not_delayed()
    {
        var clock = new FloorClock(api.Time.GetUtcNow());
        await using var host = WithFloor(clock);
        var user = await api.CreateUserAsync();

        // The clock never moves: a floor on this path would never let it finish.
        var res = await PostgresApiFactory.PostLoginAsync(PostgresApiFactory.NewClient(host), user.Email!).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Empty(clock.FloorWaits);
    }

    [Fact]
    public async Task Failed_logins_waiting_for_the_floor_hold_no_database_permit()
    {
        // One permit and one queue slot for all of /api: a wait inside the limiter would queue the second login and
        // turn the third away with a 429.
        var clock = new FloorClock(api.Time.GetUtcNow());
        await using var host = WithFloor(clock, ("RATE_LIMIT_GLOBAL_CONCURRENCY", "1"), ("RATE_LIMIT_GLOBAL_QUEUE", "1"));
        var client = PostgresApiFactory.NewClient(host);

        var waiting = new List<Task<HttpResponseMessage>>();
        for (var i = 1; i <= 9; i++)
        {
            waiting.Add(PostgresApiFactory.PostLoginAsync(client, $"nobody-{Guid.NewGuid():N}@example.com"));
            await clock.FloorWaitsAsync(i);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/me", Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct)).StatusCode);
        Assert.DoesNotContain(waiting, t => t.IsCompleted);

        clock.Advance(LoginFloorOptions.Default.Floor + LoginFloorOptions.Default.MaxJitter);
        foreach (var res in await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(30), Ct))
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    /// <summary>A host on its own clock, with the production floor the shared fixture turns off.</summary>
    private WebApplicationFactory<Program> WithFloor(FloorClock clock, params (string Key, string Value)[] settings) =>
        api.WithWebHostBuilder(b =>
        {
            foreach (var (key, value) in settings) b.UseSetting(key, value);
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<TimeProvider>();
                s.AddSingleton<TimeProvider>(clock);
                s.RemoveAll<LoginFloorOptions>();
                s.AddSingleton(LoginFloorOptions.Default);
            });
        });

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

    [Theory]
    [InlineData(true)]  // the failure-count reset after a correct password conflicts
    [InlineData(false)] // the failure-count increment after a wrong password conflicts
    public async Task A_concurrency_conflict_on_the_failure_count_fails_the_attempt(bool rightPassword)
    {
        // R218-01: as Identity's own check, a save that loses to a parallel attempt must not sign in, and must not
        // read the lockout from unsaved state. Still exactly one verification.
        var hasher = new CountingHasher();
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<IPasswordHasher<DeskUser>>();
            s.AddSingleton<IPasswordHasher<DeskUser>>(hasher);
        }));
        var user = await api.CreateUserAsync();
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.AccessFailedCount, IdentityPolicy.MaxFailedAttempts - 1), Ct);

        await using var scope = host.Services.CreateAsyncScope();
        var signIn = scope.ServiceProvider.GetRequiredService<SignInManager<DeskUser>>();
        var tracked = await signIn.UserManager.FindByIdAsync(user.Id.ToString());
        // A parallel request saved the row after this one loaded it.
        await using (var db = api.NewContext())
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid().ToString()), Ct);

        hasher.Reset();
        var result = await signIn.CheckPasswordSignInAsync(tracked!, rightPassword ? PostgresApiFactory.Password : "wrong-password-123456", lockoutOnFailure: true);

        Assert.Equal(SignInResult.Failed, result);
        Assert.Equal(1, hasher.Verifications);
        await using (var db = api.NewContext())
            Assert.Equal(IdentityPolicy.MaxFailedAttempts - 1, (await db.Users.AsNoTracking().SingleAsync(u => u.Id == user.Id, Ct)).AccessFailedCount);
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

    /// <summary>A fake clock that records every wait the size of the login floor (other timers run for minutes or hours).</summary>
    private sealed class FloorClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly ConcurrentQueue<TimeSpan> _waits = new();

        public TimeSpan[] FloorWaits => [.. _waits];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            // Recorded once the timer is armed, so a test that sees it can advance past it.
            if (dueTime > TimeSpan.Zero && dueTime <= LoginFloorOptions.Default.Floor + LoginFloorOptions.Default.MaxJitter)
                _waits.Enqueue(dueTime);
            return timer;
        }

        /// <summary>Waits (bounded, in real time) until <paramref name="count"/> floor waits have started.</summary>
        public async Task<TimeSpan[]> FloorWaitsAsync(int count)
        {
            for (var i = 0; i < 600 && _waits.Count < count; i++)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(_waits.Count >= count, $"{_waits.Count} of {count} logins reached the floor.");
            return FloorWaits;
        }
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
