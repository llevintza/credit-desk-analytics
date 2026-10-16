using System.Net;
using System.Net.Http.Json;
using Desk.Api.Admin;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;

namespace Desk.Api.Tests;

/// <summary>
/// #70 / README §7.2, §8: <c>GET /api/admin/usage</c> (admin only) reports requests per user per day, the cache hit
/// ratio and the slowest 20 requests per day from <c>app.audit</c> (ADR-0027). The exact numbers are read on their own
/// migrated database, so other tests' audit rows can't move them; the endpoint tests mark their rows with unique users.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class UsageTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateOnly Day1 = new(2026, 10, 5), Day2 = new(2026, 10, 6);

    private static DateTimeOffset At(DateOnly day, int hour, int minute = 0, int second = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute, second)), TimeSpan.Zero);

    private static AuditEntry Request(DateTimeOffset at, string user, int ms, string? cache = null, int status = 200, string endpoint = "POST /api/positions/query") =>
        new() { At = at, Kind = AuditKinds.Request, UserName = user, Endpoint = endpoint, Status = status, Ms = ms, Cache = cache, Rows = 10 };

    private sealed class Contexts(DbContextOptions<AppDbContext> options) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(options);
    }

    /// <summary>A migrated, empty database in the shared container, holding <paramref name="rows"/>.</summary>
    private async Task<UsageRepository> IsolatedAsync(params AuditEntry[] rows)
    {
        var cs = new NpgsqlConnectionStringBuilder(api.ConnectionString) { Database = $"usage_{Guid.NewGuid():N}" }.ConnectionString;
        await using (var conn = new NpgsqlConnection(api.ConnectionString))
        {
            await conn.OpenAsync(Ct);
            await using var cmd = new NpgsqlCommand($"CREATE DATABASE \"{new NpgsqlConnectionStringBuilder(cs).Database}\"", conn);
            await cmd.ExecuteNonQueryAsync(Ct);
        }
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(cs, n => n.MigrationsHistoryTable("__ef_migrations_history", AppDbContext.Schema)).Options;
        await using (var db = new AppDbContext(options))
        {
            await db.Database.MigrateAsync(Ct);
            db.Audit.AddRange(rows);
            await db.SaveChangesAsync(Ct);
        }
        return new UsageRepository(new Contexts(options));
    }

    [Fact]
    public async Task Requests_are_counted_per_user_per_utc_day_with_hits_misses_errors_and_average_ms()
    {
        var usage = await IsolatedAsync(
            Request(At(Day1, 0), "a@example.com", 10, "HIT"),
            Request(At(Day1, 23, 59, 59), "a@example.com", 21, "MISS"),
            Request(At(Day1, 12), "a@example.com", 30, status: 500),
            Request(At(Day1, 9), "b@example.com", 5, "HIT"),
            Request(At(Day2, 0), "a@example.com", 7, "HIT"), // the next UTC day starts at midnight UTC
            new AuditEntry { At = At(Day1, 8), Kind = AuditKinds.LoginSuccess, UserName = "a@example.com", Endpoint = "POST /api/auth/login", Status = 200 },
            new AuditEntry { At = At(Day1, 8), Kind = AuditKinds.LoginFailure, UserName = "c@example.com", Endpoint = "POST /api/auth/login", Status = 401 },
            Request(At(Day1.AddDays(-1), 23, 59, 59), "a@example.com", 900, "MISS"), // before the window
            Request(At(Day2.AddDays(1), 0), "a@example.com", 900, "MISS"));          // after it

        var report = await usage.ReadAsync(Day1, Day2, Ct);

        Assert.Equal((Day1, Day2), (report.From, report.To));
        Assert.Equal(
        [
            new UserDayUsage(Day2, "a@example.com", 1, 1, 0, 0, 7),
            new UserDayUsage(Day1, "a@example.com", 3, 1, 1, 1, 20), // round((10 + 21 + 30) / 3) = 20
            new UserDayUsage(Day1, "b@example.com", 1, 1, 0, 0, 5),
        ], report.Users);
        Assert.Equal([new DayUsage(Day2, 1, 1, 0, 1.0), new DayUsage(Day1, 4, 2, 1, 2.0 / 3)], report.Days);
        Assert.Equal((5, 3, 1), (report.Requests, report.Hits, report.Misses));
        Assert.Equal(0.75, report.HitRatio);
    }

    [Fact]
    public async Task Hit_ratio_is_null_not_NaN_when_nothing_cacheable_ran()
    {
        var usage = await IsolatedAsync(Request(At(Day1, 10), "a@example.com", 10, cache: null));

        var report = await usage.ReadAsync(Day1, Day1, Ct);

        Assert.Null(report.HitRatio);
        Assert.Null(Assert.Single(report.Days).HitRatio);
        Assert.Equal(1, report.Requests);
    }

    [Fact]
    public async Task An_empty_window_reports_zeros_and_no_rows()
    {
        var usage = await IsolatedAsync();

        var report = await usage.ReadAsync(Day1, Day2, Ct);

        Assert.Equal((0, 0, 0, (double?)null), (report.Requests, report.Hits, report.Misses, report.HitRatio));
        Assert.Empty(report.Days);
        Assert.Empty(report.Users);
        Assert.Empty(report.Slowest);
    }

    [Fact]
    public async Task Slowest_are_the_top_20_of_each_day_slowest_first_ties_in_insert_order()
    {
        // Day 1: 25 requests, 100..124 ms, plus a tie at the top; day 2: 3 requests; logins never count.
        var rows = Enumerable.Range(0, 25).Select(i => Request(At(Day1, 1, i), $"u{i}@example.com", 100 + i)).ToList();
        rows.Add(Request(At(Day1, 2), "tie@example.com", 124, "HIT", endpoint: "GET /api/deals"));
        rows.AddRange(Enumerable.Range(0, 3).Select(i => Request(At(Day2, 3, i), "d2@example.com", 50 - i)));
        rows.Add(new AuditEntry { At = At(Day2, 4), Kind = AuditKinds.LoginFailure, UserName = "x@example.com", Endpoint = "POST /api/auth/login", Status = 401, Ms = 99_999 });
        var usage = await IsolatedAsync([.. rows]);

        var slowest = (await usage.ReadAsync(Day1, Day2, Ct)).Slowest;

        Assert.Equal([Day2, Day2, Day2], slowest.Take(3).Select(s => s.Day));
        Assert.Equal([50, 49, 48], slowest.Take(3).Select(s => s.Ms));
        var day1 = slowest.Skip(3).ToList();
        Assert.Equal(UsageRepository.SlowestPerDay, day1.Count);
        Assert.All(day1, s => Assert.Equal(Day1, s.Day));
        Assert.Equal([124, 124, 123, 122], day1.Take(4).Select(s => s.Ms));
        Assert.Equal(["u24@example.com", "tie@example.com"], day1.Take(2).Select(s => s.UserName)); // same ms: lower id first
        Assert.Equal(106, day1[^1].Ms); // 124 twice, then 123 … 106: 20 rows
        var tie = day1[1];
        Assert.Equal((At(Day1, 2), "GET /api/deals", 200, (int?)10, "HIT"), (tie.At, tie.Endpoint, tie.Status, tie.Rows, tie.Cache));
    }

    // --- the endpoint

    [Fact]
    public async Task Usage_is_for_admins_only()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.NewClient().GetAsync("/api/admin/usage", Ct)).StatusCode);
        var (viewer, _, _) = await api.SignedInAsync(Roles.Viewer);
        Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/admin/usage", Ct)).StatusCode);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("32")]
    [InlineData("abc")]
    public async Task Days_outside_1_to_31_is_a_400_problem(string days)
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);

        var res = await admin.GetAsync($"/api/admin/usage?days={days}", Ct);

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal("application/problem+json", res.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Admin_gets_the_report_for_the_last_days_with_their_rows_uncached()
    {
        // Unique users, slower than anything another test writes, so they lead their day whatever else is in app.audit.
        var tag = Guid.NewGuid().ToString("N")[..8];
        string slow = $"slow-{tag}@example.com", fast = $"fast-{tag}@example.com";
        var rows = Enumerable.Range(0, 21).Select(i => Request(At(Day1, 10, i), slow, 5_000_000 + i, "MISS")).ToList();
        rows.Add(Request(At(Day1, 11), fast, 3, "HIT"));
        await using (var db = api.NewContext())
        {
            db.Audit.AddRange(rows);
            await db.SaveChangesAsync(Ct);
        }
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);

        var res = await admin.GetAsync("/api/admin/usage", Ct);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("no-store", res.Headers.CacheControl?.ToString());
        Assert.Contains("db;dur=", res.Headers.GetValues("Server-Timing").Single());
        var report = (await res.Content.ReadFromJsonAsync<UsageReport>(Ct))!;
        var today = DateOnly.FromDateTime(PostgresApiFactory.Start.UtcDateTime);
        Assert.Equal((today.AddDays(1 - AdminEndpoints.DefaultUsageDays), today), (report.From, report.To));
        Assert.Equal(new UserDayUsage(Day1, slow, 21, 0, 21, 0, 5_000_010), report.Users.Single(u => u.UserName == slow));
        Assert.Equal(new UserDayUsage(Day1, fast, 1, 1, 0, 0, 3), report.Users.Single(u => u.UserName == fast));
        var day1Slowest = report.Slowest.Where(s => s.Day == Day1).ToList();
        Assert.Equal(20, day1Slowest.Count);
        Assert.All(day1Slowest, s => Assert.Equal(slow, s.UserName));
        Assert.Equal(5_000_020, day1Slowest[0].Ms);
        Assert.Equal(5_000_001, day1Slowest[^1].Ms);

        // Its own audit row carries no cache status, so reading the page doesn't move the hit ratio.
        var mine = await api.WaitForAuditAsync(a => a.Endpoint == "GET /api/admin/usage" && a.Status == 200);
        Assert.All(mine, a => Assert.Null(a.Cache));
    }

    [Fact]
    public async Task Days_narrows_the_window_and_the_retention_window_caps_it()
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        var today = DateOnly.FromDateTime(PostgresApiFactory.Start.UtcDateTime);

        var one = (await admin.GetFromJsonAsync<UsageReport>("/api/admin/usage?days=1", Ct))!;
        Assert.Equal((today, today), (one.From, one.To));
        Assert.All(one.Users, u => Assert.Equal(today, u.Day));

        await using var host = api.WithSettings(("AUDIT_RETENTION_DAYS", "3"));
        var client = PostgresApiFactory.NewClient(host);
        var user = await api.CreateUserAsync(Roles.Admin);
        await PostgresApiFactory.LoginAsync(client, user.Email!);
        var capped = (await client.GetFromJsonAsync<UsageReport>($"/api/admin/usage?days={AdminEndpoints.MaxUsageDays}", Ct))!;
        Assert.Equal((today.AddDays(-2), today), (capped.From, capped.To));
    }
}
