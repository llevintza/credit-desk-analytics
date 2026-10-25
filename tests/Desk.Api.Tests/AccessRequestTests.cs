using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Desk.Api.AccessRequests;
using Desk.Api.Auth;
using Desk.Api.Hardening;
using Desk.Api.Limits;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>
/// ADR-0029, #538: the anonymous <c>POST /api/access-requests</c>. One identical 202 for every outcome a caller must
/// not tell apart (and the same floor wait), the refusals that never reach the database, the pending cap, the off
/// switch, and no email or name in logs or the audit.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class AccessRequestTests(PostgresApiFactory api)
{
    private const string Url = "/api/access-requests";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewEmail() => $"visitor-{Guid.NewGuid():N}@example.com";

    /// <summary>A form a person filled in: no honeypot, rendered 10 s before <paramref name="now"/>.</summary>
    private static AccessRequestBody Form(DateTimeOffset now, string email, string? name = null, string? purpose = null) =>
        new(email, name, purpose, Website: null, StartedAt: now.AddSeconds(-10));

    private AccessRequestBody Form(string email, string? name = null, string? purpose = null) => Form(api.Time.GetUtcNow(), email, name, purpose);

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, AccessRequestBody body) => client.PostAsJsonAsync(Url, body, Ct);

    private async Task<List<AccessRequest>> RowsAsync(string email)
    {
        await using var db = api.NewContext();
        return await db.AccessRequests.AsNoTracking().Where(r => r.Email == email).ToListAsync(Ct);
    }

    /// <summary>Status, every header and the body: what a caller could compare.</summary>
    private static async Task<string> FingerprintAsync(HttpResponseMessage res) =>
        string.Join("\n", new[] { ((int)res.StatusCode).ToString(System.Globalization.CultureInfo.InvariantCulture) }
            .Concat(res.Headers.Concat(res.Content.Headers).OrderBy(h => h.Key, StringComparer.Ordinal).Select(h => $"{h.Key}: {string.Join(",", h.Value)}"))
            .Append(await res.Content.ReadAsStringAsync(Ct)));

    [Fact]
    public async Task New_duplicate_and_existing_account_submissions_get_an_identical_202()
    {
        var client = api.NewClient();
        var email = NewEmail();
        var account = await api.CreateUserAsync();

        var created = await PostAsync(client, Form(email, "A Visitor", "Evaluating the grid"));
        var duplicate = await PostAsync(client, Form(email.ToUpperInvariant(), "Someone else", "Again"));
        var existing = await PostAsync(client, Form(account.Email!));
        var honeypot = await PostAsync(client, Form(NewEmail()) with { Website = "https://spam.test" });
        var tooFast = await PostAsync(client, Form(NewEmail()) with { StartedAt = api.Time.GetUtcNow().AddSeconds(-1) });

        Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
        var expected = await FingerprintAsync(created);
        foreach (var other in new[] { duplicate, existing, honeypot, tooFast })
            Assert.Equal(expected, await FingerprintAsync(other));
        Assert.Null(created.Headers.Location);
        using var body = JsonDocument.Parse(await created.Content.ReadAsStringAsync(Ct));
        Assert.Equal("received", body.RootElement.GetProperty("status").GetString());

        var row = Assert.Single(await RowsAsync(email));
        Assert.Equal((email, "A Visitor", "Evaluating the grid", AccessRequestStatus.Pending), (row.Email, row.DisplayName, row.Purpose, row.Status));
        Assert.Equal(api.Time.GetUtcNow(), row.CreatedAt);
        Assert.Null(row.DecidedAt);
        Assert.Null(row.AccountId);
        Assert.Empty(await RowsAsync(account.Email!));
    }

    [Fact]
    public async Task The_email_is_stored_trimmed_and_lower_case_and_blank_text_as_null()
    {
        var email = NewEmail();
        var res = await PostAsync(api.NewClient(), Form($"  {email.ToUpperInvariant()} ", "   ", "  why  "));

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        var row = Assert.Single(await RowsAsync(email));
        Assert.Equal(email, row.Email);
        Assert.Null(row.DisplayName);
        Assert.Equal("why", row.Purpose);
    }

    [Fact]
    public async Task A_decided_request_does_not_stop_a_new_one()
    {
        var email = NewEmail();
        await using (var db = api.NewContext())
        {
            db.AccessRequests.Add(new AccessRequest { Email = email, Status = AccessRequestStatus.Rejected, CreatedAt = api.Time.GetUtcNow().AddDays(-1) });
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(api.NewClient(), Form(email))).StatusCode);

        Assert.Equal([AccessRequestStatus.Pending, AccessRequestStatus.Rejected], (await RowsAsync(email)).Select(r => r.Status).Order());
    }

    public static TheoryData<string> Junk => ["honeypot", "too fast", "no start time", "started in the future"];

    [Theory]
    [MemberData(nameof(Junk))]
    public async Task Junk_never_reaches_the_database_and_gets_the_same_202(string junk)
    {
        await using var host = api.WithSettings();
        var client = PostgresApiFactory.NewClient(host);
        var now = api.Time.GetUtcNow();
        var email = NewEmail();
        var form = Form(now, email);
        form = junk switch
        {
            "honeypot" => form with { Website = "x" },
            "too fast" => form with { StartedAt = now - AccessRequestOptions.MinFillTime + TimeSpan.FromMilliseconds(1) },
            "no start time" => form with { StartedAt = null },
            _ => form with { StartedAt = now.AddMinutes(1) },
        };
        var before = api.ConnectionsOpened(host);

        var res = await PostAsync(client, form);

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(before, api.ConnectionsOpened(host));
        Assert.Empty(await RowsAsync(email));
    }

    [Fact]
    public async Task A_form_filled_in_exactly_the_minimum_time_is_stored()
    {
        var email = NewEmail();
        var res = await PostAsync(api.NewClient(), Form(email) with { StartedAt = api.Time.GetUtcNow() - AccessRequestOptions.MinFillTime });

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Single(await RowsAsync(email));
    }

    [Fact]
    public async Task The_fourth_request_in_an_hour_from_one_address_gets_429_without_reaching_the_database()
    {
        await using var host = api.WithSettings(("RATE_LIMIT_ACCESS_REQUESTS_PER_IP_PER_HOUR", "3"));
        var client = PostgresApiFactory.NewClient(host);
        var before = api.ConnectionsOpened(host);
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, Form(NewEmail()) with { Website = "bot" })).StatusCode);

        var email = NewEmail();
        var fourth = await PostAsync(client, Form(email));

        Assert.Equal(HttpStatusCode.TooManyRequests, fourth.StatusCode);
        Assert.True(int.Parse(fourth.Headers.RetryAfter!.ToString(), System.Globalization.CultureInfo.InvariantCulture) > 60);
        Assert.Equal(before, api.ConnectionsOpened(host));
        Assert.Empty(await RowsAsync(email));
        // The probe is outside the window.
        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(Url, Ct)).StatusCode);
    }

    public static TheoryData<string?, string?, string?, string> Invalid => new()
    {
        { null, null, null, "email" },
        { "", null, null, "email" },
        { "not-an-email", null, null, "email" },
        { "two@at@example.com", null, null, "email" },
        { "A Person <a@example.com>", null, null, "email" },
        { "with space@example.com", null, null, "email" },
        { new string('a', 250) + "@x.io", null, null, "email" },
        { "a@example.com", new string('n', 501), null, "name" },
        { "a@example.com", null, new string('p', 501), "purpose" },
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task An_invalid_form_gets_400_without_reaching_the_database(string? email, string? name, string? purpose, string field)
    {
        await using var host = api.WithSettings();
        var client = PostgresApiFactory.NewClient(host);
        var before = api.ConnectionsOpened(host);

        var res = await PostAsync(client, new AccessRequestBody(email, name, purpose, null, api.Time.GetUtcNow().AddSeconds(-10)));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var problem = JsonDocument.Parse(await res.Content.ReadAsStringAsync(Ct));
        Assert.Equal([field], problem.RootElement.GetProperty("errors").EnumerateObject().Select(p => p.Name));
        Assert.Equal(before, api.ConnectionsOpened(host));
    }

    [Fact]
    public async Task Text_of_exactly_500_characters_is_accepted()
    {
        var email = NewEmail();
        var res = await PostAsync(api.NewClient(), Form(email, new string('n', 500), new string('p', 500)));

        Assert.Equal(HttpStatusCode.Accepted, res.StatusCode);
        Assert.Equal(500, Assert.Single(await RowsAsync(email)).Purpose!.Length);
    }

    [Fact]
    public async Task At_the_pending_cap_the_request_gets_503_with_retry_after_and_is_not_stored()
    {
        await using (var db = api.NewContext())
        {
            db.AccessRequests.Add(new AccessRequest { Email = NewEmail(), CreatedAt = api.Time.GetUtcNow() });
            await db.SaveChangesAsync(Ct);
        }
        await using var host = api.WithSettings((AccessRequestOptions.MaxPendingConfigKey, "1"));
        var email = NewEmail();

        var res = await PostAsync(PostgresApiFactory.NewClient(host), Form(email));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Equal("3600", res.Headers.RetryAfter!.ToString());
        Assert.Empty(await RowsAsync(email));
        var audit = await api.WaitForAuditAsync(a => a.Kind == AuditKinds.AccessRequest && a.Status == 503);
        Assert.All(audit, a => Assert.Null(a.UserName));
    }

    [Fact]
    public async Task Maintenance_mode_answers_503_without_opening_a_connection()
    {
        await using var host = api.WithSettings((MaintenanceMode.ConfigKey, "true"));
        var client = PostgresApiFactory.NewClient(host);
        var before = api.ConnectionsOpened(host);
        var email = NewEmail();

        var res = await PostAsync(client, Form(email));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        Assert.Contains(MaintenanceMode.Detail, await res.Content.ReadAsStringAsync(Ct));
        Assert.Equal(before, api.ConnectionsOpened(host));
        Assert.Empty(await RowsAsync(email));
    }

    [Fact]
    public async Task The_probe_answers_204_without_opening_a_connection()
    {
        await using var host = api.WithSettings();
        var client = PostgresApiFactory.NewClient(host);
        var before = api.ConnectionsOpened(host);

        Assert.Equal(HttpStatusCode.NoContent, (await client.GetAsync(Url, Ct)).StatusCode);
        Assert.Equal(before, api.ConnectionsOpened(host));
    }

    [Theory]
    [InlineData("")]
    [InlineData("false")]
    [InlineData("yes")]
    public async Task Switched_off_the_endpoints_are_404_and_left_out_of_the_openapi_document(string? value)
    {
        await using var host = api.WithWebHostBuilder(b => b.UseSetting(AccessRequestOptions.EnabledConfigKey, value));
        var client = PostgresApiFactory.NewClient(host);
        var before = api.ConnectionsOpened(host);

        Assert.Equal(HttpStatusCode.NotFound, (await PostAsync(client, Form(NewEmail()))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Url, Ct)).StatusCode);
        Assert.Equal(before, api.ConnectionsOpened(host));

        var user = await api.CreateUserAsync(Roles.Admin);
        await PostgresApiFactory.LoginAsync(client, user.Email!);
        var doc = await client.GetStringAsync("/openapi/v1.json", Ct);
        Assert.DoesNotContain("access-requests", doc, StringComparison.Ordinal);
        Assert.DoesNotContain("RequestAccess", doc, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switched_on_the_endpoints_are_in_the_openapi_document()
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/openapi/v1.json", Ct));
        var path = doc.RootElement.GetProperty("paths").GetProperty(Url);
        Assert.Equal("RequestAccess", path.GetProperty("post").GetProperty("operationId").GetString());
        Assert.Equal("GetAccessRequestsStatus", path.GetProperty("get").GetProperty("operationId").GetString());
        Assert.False(path.GetProperty("post").TryGetProperty("security", out _));
    }

    [Fact]
    public async Task No_email_or_name_reaches_the_logs_or_the_audit()
    {
        var logs = new CapturingLoggerProvider();
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s => s.AddSingleton<ILoggerProvider>(logs)));
        var client = PostgresApiFactory.NewClient(host);
        var token = Guid.NewGuid().ToString("N");
        var email = $"secret-{token}@example.com";
        var name = $"Hidden Name {token}";
        var account = await api.CreateUserAsync();

        foreach (var form in new[]
        {
            Form(email, name, "purpose"),            // stored
            Form(email, name, "purpose"),            // duplicate
            Form(account.Email!, name, "purpose"),   // existing account
            Form(email, name) with { Website = "x" },
            Form(email, new string('n', 501)),       // invalid
        })
            await PostAsync(client, form);

        // The three that reached the database (stored, duplicate, existing account); refused junk writes no row.
        var audit = await api.WaitForAuditAsync(a => a.Kind == AuditKinds.AccessRequest, atLeast: 3);
        Assert.All(audit, a => Assert.Equal((null, AccessRequestEndpoints.AuditEndpoint), (a.UserName, a.Endpoint)));
        await using (var db = api.NewContext())
        {
            var leaked = await db.Audit.AsNoTracking().Where(a => (a.UserName != null && a.UserName.Contains(token)) || a.Endpoint.Contains(token)).CountAsync(Ct);
            Assert.Equal(0, leaked);
        }

        var lines = logs.Lines.ToArray();
        Assert.Contains(lines, l => l.Category == AccessRequestEndpoints.LogCategory && l.Text == "Access request stored");
        Assert.Contains(lines, l => l.Text == "Access request duplicate");
        Assert.Contains(lines, l => l.Text == "Access request existing_account");
        Assert.Contains(lines, l => l.Text == "Access request honeypot");
        Assert.Contains(lines, l => l.Text == "Access request invalid");
        Assert.DoesNotContain(lines, l => l.Text.Contains(token, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Text.Contains(account.Email!, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_signed_in_caller_is_audited_without_a_user_name()
    {
        var (client, _, user) = await api.SignedInAsync();

        Assert.Equal(HttpStatusCode.Accepted, (await PostAsync(client, Form(NewEmail()))).StatusCode);

        await api.WaitForAuditAsync(a => a.Kind == AuditKinds.AccessRequest);
        await using var db = api.NewContext();
        Assert.False(await db.Audit.AsNoTracking().AnyAsync(a => a.UserName == user.Email && a.Endpoint.Contains("access-requests"), Ct));
    }

    [Fact]
    public async Task The_pages_origin_may_call_it_and_its_preflight_is_answered_before_the_limiter()
    {
        const string pages = "https://pages.example.test";
        await using var host = api.WithSettings((CorsSetup.ConfigKey, pages), ("RATE_LIMIT_ACCESS_REQUESTS_PER_IP_PER_HOUR", "1"));
        var client = PostgresApiFactory.NewClient(host);

        for (var i = 0; i < 2; i++)
        {
            using var preflight = new HttpRequestMessage(HttpMethod.Options, Url);
            preflight.Headers.Add("Origin", pages);
            preflight.Headers.Add("Access-Control-Request-Method", "POST");
            preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
            var res = await client.SendAsync(preflight, Ct);
            Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);
            Assert.Equal(pages, res.Headers.GetValues("Access-Control-Allow-Origin").Single());
            Assert.Contains("POST", res.Headers.GetValues("Access-Control-Allow-Methods").Single(), StringComparison.Ordinal);
        }

        foreach (var expected in new[] { HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests })
        {
            using var post = new HttpRequestMessage(HttpMethod.Post, Url) { Content = JsonContent.Create(Form(NewEmail()) with { Website = "x" }) };
            post.Headers.Add("Origin", pages);
            var res = await client.SendAsync(post, Ct);
            Assert.Equal(expected, res.StatusCode);
            Assert.Equal(pages, res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        }
    }

    public enum Outcome { New, Duplicate, ExistingAccount, Honeypot, TooFast }

    [Theory]
    [InlineData(Outcome.New)]
    [InlineData(Outcome.Duplicate)]
    [InlineData(Outcome.ExistingAccount)]
    [InlineData(Outcome.Honeypot)]
    [InlineData(Outcome.TooFast)]
    public async Task Every_202_answers_after_the_login_floor(Outcome outcome)
    {
        // ADR-0029 §1: the timing bound. Whatever the path did, the 202 isn't sent before the floor, so the time a
        // caller measures is the floor plus jitter for all of them.
        var clock = new FloorClock(api.Time.GetUtcNow());
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(clock);
            s.RemoveAll<LoginFloorOptions>();
            s.AddSingleton(LoginFloorOptions.Default);
        }));
        var client = PostgresApiFactory.NewClient(host);
        var now = clock.GetUtcNow();
        var email = NewEmail();
        if (outcome == Outcome.Duplicate)
            await using (var db = api.NewContext())
            {
                db.AccessRequests.Add(new AccessRequest { Email = email, CreatedAt = now });
                await db.SaveChangesAsync(Ct);
            }
        if (outcome == Outcome.ExistingAccount)
            email = (await api.CreateUserAsync()).Email!;
        var form = outcome switch
        {
            Outcome.Honeypot => Form(now, email) with { Website = "x" },
            Outcome.TooFast => Form(now, email) with { StartedAt = now },
            _ => Form(now, email),
        };

        var pending = PostAsync(client, form);

        var wait = Assert.Single(await clock.FloorWaitsAsync(1));
        Assert.InRange(wait, LoginFloorOptions.Default.Floor, LoginFloorOptions.Default.Floor + LoginFloorOptions.Default.MaxJitter);
        clock.Advance(wait - TimeSpan.FromTicks(1));
        Assert.False(pending.IsCompleted);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.Equal(HttpStatusCode.Accepted, (await pending.WaitAsync(TimeSpan.FromSeconds(30), Ct)).StatusCode);
    }

    [Fact]
    public async Task A_400_is_not_held_back_by_the_floor()
    {
        var clock = new FloorClock(api.Time.GetUtcNow());
        await using var host = api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<TimeProvider>();
            s.AddSingleton<TimeProvider>(clock);
            s.RemoveAll<LoginFloorOptions>();
            s.AddSingleton(LoginFloorOptions.Default);
        }));

        // The clock never moves: a floor on this path would never let it finish.
        var res = await PostAsync(PostgresApiFactory.NewClient(host), Form(clock.GetUtcNow(), "nope")).WaitAsync(TimeSpan.FromSeconds(30), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(clock.FloorWaits);
    }

    [Theory]
    [InlineData(null, AccessRequestOptions.DefaultMaxPending, false)]
    [InlineData("", AccessRequestOptions.DefaultMaxPending, false)]
    [InlineData("1", 1, false)]
    [InlineData("1000", 1000, false)]
    [InlineData("0", AccessRequestOptions.DefaultMaxPending, true)]
    [InlineData("1001", AccessRequestOptions.DefaultMaxPending, true)]
    [InlineData("lots", AccessRequestOptions.DefaultMaxPending, true)]
    public void The_pending_cap_is_a_whole_number_from_1_to_1000_or_the_default_with_a_warning(string? value, int expected, bool warns)
    {
        var logs = new CapturingLoggerProvider();
        using var factory = LoggerFactory.Create(l => l.AddProvider(logs));
        var config = new ConfigurationBuilder().AddInMemoryCollection([new(AccessRequestOptions.MaxPendingConfigKey, value)]).Build();

        var options = new AccessRequestOptions(config, factory.CreateLogger<AccessRequestOptions>());

        Assert.Equal(expected, options.MaxPending);
        Assert.Equal(warns, logs.Lines.Any(l => l.Level == LogLevel.Warning && l.Text.Contains(AccessRequestOptions.MaxPendingConfigKey, StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("false", false)]
    [InlineData("1", false)]
    [InlineData("on", false)]
    [InlineData("true", true)]
    [InlineData("True", true)]
    public void Only_a_boolean_true_switches_it_on(string? value, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new(AccessRequestOptions.EnabledConfigKey, value)]).Build();
        Assert.Equal(expected, new AccessRequestOptions(config, NullLogger<AccessRequestOptions>.Instance).Enabled);
    }

    [Fact]
    public void The_per_address_window_defaults_to_three_an_hour()
    {
        Assert.Equal(3, LimitsOptions.From(new ConfigurationBuilder().Build()).AccessRequestsPerIpPerHour);
    }

    /// <summary>A fake clock that records every wait the size of the login floor (other timers run for minutes or hours).</summary>
    private sealed class FloorClock(DateTimeOffset start) : FakeTimeProvider(start)
    {
        private readonly ConcurrentQueue<TimeSpan> _waits = new();

        public TimeSpan[] FloorWaits => [.. _waits];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero && dueTime <= LoginFloorOptions.Default.Floor + LoginFloorOptions.Default.MaxJitter)
                _waits.Enqueue(dueTime);
            return timer;
        }

        public async Task<TimeSpan[]> FloorWaitsAsync(int count)
        {
            for (var i = 0; i < 600 && _waits.Count < count; i++)
                await Task.Delay(50, TestContext.Current.CancellationToken);
            Assert.True(_waits.Count >= count, $"{_waits.Count} of {count} requests reached the floor.");
            return FloorWaits;
        }
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<(string Category, LogLevel Level, string Text)> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

        public void Dispose() { }

        private sealed class Logger(CapturingLoggerProvider owner, string category) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
                owner.Lines.Enqueue((category, logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
        }
    }
}
