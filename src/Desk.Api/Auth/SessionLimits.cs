using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Auth;

/// <summary>
/// The rules a bearer token can't carry by itself (README §7.1, ADR-0024), checked on every authenticated request
/// and at refresh: the account's expiry, the 24 h absolute limit from the sign-in, and logout. A token that fails
/// any of them is treated as no token, so the caller gets a 401 and the SPA signs out. No database read.
/// </summary>
public sealed class SessionLimits(RequestDelegate next, SessionRevocations revoked, TimeProvider time)
{
    public Task InvokeAsync(HttpContext http)
    {
        if (http.User.IsSignedIn() && !IsLive(http.User, time.GetUtcNow(), revoked))
            http.User = new ClaimsPrincipal(new ClaimsIdentity());
        return next(http);
    }

    /// <summary>True while the account hasn't expired, the sign-in is under 24 h old, and it hasn't been logged out.</summary>
    public static bool IsLive(ClaimsPrincipal user, DateTimeOffset now, SessionRevocations revoked) =>
        DeskClaimsFactory.ReadExpiry(user) is { } expiry && now < expiry
        && ReadAuthTime(user) is { } authTime && now - authTime < AuthSetup.AbsoluteExpiry
        && user.FindFirstValue(AuthSetup.SessionIdClaim) is { Length: > 0 } sid && !revoked.IsRevoked(sid, now);

    public static DateTimeOffset? ReadAuthTime(ClaimsPrincipal user) =>
        DateTimeOffset.TryParse(user.FindFirstValue(AuthSetup.AuthTimeClaim), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
}

/// <summary>
/// This instance's cache of ended sessions, checked on every authenticated request with no database read. Filled by
/// <see cref="SessionStore"/>: by a logout or a replay here, and by a refresh that finds its session ended in the
/// database (another instance's logout, or one from before a restart). Kept until the session's 24 h limit.
/// Limit (ADR-0024, #354): an access token this instance hasn't seen revoked works until its own expiry (5 min);
/// its refresh token is refused everywhere.
/// </summary>
public sealed class SessionRevocations
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _until = new(StringComparer.Ordinal);

    public void Revoke(string sid, DateTimeOffset until, DateTimeOffset now)
    {
        // Every logout prunes, so the map holds at most the sessions logged out in the last 24 h.
        foreach (var (key, end) in _until)
            if (end <= now) _until.TryRemove(key, out _);
        if (until > now) _until[sid] = until;
    }

    public bool IsRevoked(string sid, DateTimeOffset now) => _until.TryGetValue(sid, out var until) && now < until;

    public int Count => _until.Count;
}

/// <summary>
/// The sessions' server-side state in Postgres (<c>auth.sessions</c>, #354, ADR-0024): single-use refresh tokens with
/// reuse detection (#353), and logout. Each token pair carries a refresh id (<see cref="AuthSetup.RefreshIdClaim"/>);
/// a session's row holds the one id whose refresh token may still be swapped. A refresh that presents any other id
/// replays a token already swapped: a stolen copy, or the owner's own after a thief swapped it first. The caller then
/// ends the whole session, for both holders.
/// One exception, the grace window: for a short time after a swap, the id just swapped gets a pair for the current id
/// without a new swap. A page reload can abort a refresh after the server swapped the token, and the reloaded page
/// then sends the old token again; two racing refreshes with one token end up on one id the same way.
/// Read and written only at login, refresh and logout (each already a database call), never per request: the
/// per-request check is <see cref="SessionRevocations"/>, this instance's cache of the revocations it has seen.
/// A restart or a second instance shares the rows, so neither forgets a logout or a swap. Only ids and times are
/// kept, never token values.
/// </summary>
public sealed class SessionStore(TimeSpan grace, IDbContextFactory<AppDbContext> contexts, SessionRevocations revoked)
{
    /// <summary>The grace window when <see cref="GraceSecondsKey"/> is unset or not a positive number of seconds.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);
    /// <summary>The longest grace window a setting can ask for; a larger value is clamped to it.</summary>
    public static readonly TimeSpan MaxGrace = TimeSpan.FromSeconds(120);
    public const string GraceSecondsKey = "AUTH_REFRESH_REUSE_GRACE_SECONDS";

    public TimeSpan Grace => grace;

    /// <summary>
    /// The grace window from <see cref="GraceSecondsKey"/>. As with LimitsOptions, a typo or 0 can't turn it off:
    /// anything that isn't a positive number gives <see cref="DefaultGrace"/>. Above <see cref="MaxGrace"/> it's clamped,
    /// so a typo can't stretch the window in which a racing thief goes unseen.
    /// </summary>
    public static TimeSpan GraceFrom(IConfiguration config) =>
        double.TryParse(config[GraceSecondsKey], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0 && double.IsFinite(v)
            ? TimeSpan.FromSeconds(Math.Min(v, MaxGrace.TotalSeconds))
            : DefaultGrace;

    /// <summary>A new sign-in: <paramref name="rid"/> is its current id until <paramref name="until"/> (its 24 h limit).</summary>
    public async Task IssueAsync(string sid, string rid, DateTimeOffset until, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        // Every sign-in prunes, so the table holds at most the sessions started in the last 24 h.
        await db.AuthSessions.Where(s => s.ExpiresAt <= now).ExecuteDeleteAsync(ct);
        db.AuthSessions.Add(new AuthSession { Sid = sid, Rid = rid, RotatedAt = now, ExpiresAt = until });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The refresh id the new token pair carries, or a null id when the session is over (the caller refuses):
    /// <list type="bullet">
    /// <item>the current id is swapped for <paramref name="next"/>, which is returned;</item>
    /// <item>the id swapped last, within the grace window, gets the current id back and swaps nothing;</item>
    /// <item>a session with no row (a token from before #354, or one pruned) is believed once, as a new chain;</item>
    /// <item>a session already ended is refused, and remembered here so its access tokens are refused too;</item>
    /// <item>any other id, or the last one after the window, is a replay: the session is ended.</item>
    /// </list>
    /// Of concurrent refreshes with one token, on any instance, exactly one swaps (a conditional UPDATE); the others
    /// get the id it swapped to.
    /// </summary>
    public async Task<Rotation> RotateAsync(string sid, string? presented, string next, DateTimeOffset until, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = await contexts.CreateDbContextAsync(ct);
        while (true)
        {
            if (presented is not null && await db.AuthSessions
                    .Where(s => s.Sid == sid && s.RevokedAt == null && s.Rid == presented)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(s => s.PreviousRid, s => s.Rid)
                        .SetProperty(s => s.Rid, next)
                        .SetProperty(s => s.RotatedAt, now), ct) == 1)
                return new Rotation(next, Replayed: false);

            var row = await db.AuthSessions.AsNoTracking().Where(s => s.Sid == sid)
                .Select(s => new { s.Rid, s.PreviousRid, s.RotatedAt, s.RevokedAt })
                .SingleOrDefaultAsync(ct);
            if (row is null)
            {
                if (await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO auth.sessions (sid, rid, previous_rid, rotated_at, expires_at)
                    VALUES ({sid}, {next}, {presented}, {now}, {until})
                    ON CONFLICT (sid) DO NOTHING
                    """, ct) == 1)
                    return new Rotation(next, Replayed: false);
                continue; // another refresh created it first: look again
            }
            if (row.RevokedAt is not null)
            {
                revoked.Revoke(sid, until, now);
                return new Rotation(null, Replayed: false);
            }
            if (presented is not null && string.Equals(row.Rid, presented, StringComparison.Ordinal))
                continue; // can't stay so: the UPDATE above matches it
            if (presented is not null && string.Equals(row.PreviousRid, presented, StringComparison.Ordinal) && now - row.RotatedAt < grace)
                return new Rotation(row.Rid, Replayed: false);

            await RevokeAsync(sid, until, now);
            return new Rotation(null, Replayed: true);
        }
    }

    /// <summary>
    /// Ends the session until <paramref name="until"/> (its 24 h limit): logout, or a replay. This instance refuses its
    /// tokens at once; every instance refuses its refresh token, and so ends it within one access-token life (5 min).
    /// <para>
    /// Takes no request token on purpose: a client that drops the connection (a replaying thief, or the reload of
    /// #366) must not cancel the write and leave the session ended only in this process's memory, to come back on
    /// another instance or after a restart. The write is bounded by the command timeout instead.
    /// </para>
    /// </summary>
    public async Task RevokeAsync(string sid, DateTimeOffset until, DateTimeOffset now)
    {
        revoked.Revoke(sid, until, now);
        if (until <= now)
            return;
        await using var db = await contexts.CreateDbContextAsync(CancellationToken.None);
        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO auth.sessions (sid, rotated_at, expires_at, revoked_at)
            VALUES ({sid}, {now}, {until}, {now})
            ON CONFLICT (sid) DO UPDATE SET revoked_at = COALESCE(sessions.revoked_at, EXCLUDED.revoked_at)
            """, CancellationToken.None);
    }
}

/// <param name="Rid">The refresh id for the new token pair; null when the refresh is refused.</param>
/// <param name="Replayed">Refused because a swapped token came back: the session was ended just now.</param>
public readonly record struct Rotation(string? Rid, bool Replayed);
