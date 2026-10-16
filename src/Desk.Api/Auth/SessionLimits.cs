using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using Desk.Data.Auth;

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
        // A token without an account id is refused here, before any limiter (#399). Dropped to anonymous instead, it
        // would be keyed on the client address and spend the anonymous callers' buckets there (#361).
        if (http.User.HasNoAccountId())
            return PrincipalExtensions.NoAccountId().ExecuteAsync(http);
        if (http.User.IsSignedIn() && !IsLive(http.User, time.GetUtcNow(), revoked))
            http.User = new ClaimsPrincipal(new ClaimsIdentity());
        return next(http);
    }

    /// <summary>
    /// True while the token names its account, the account hasn't expired, the sign-in is under 24 h old, and it
    /// hasn't been logged out. Also checked at refresh.
    /// </summary>
    public static bool IsLive(ClaimsPrincipal user, DateTimeOffset now, SessionRevocations revoked) =>
        user.TryGetAccountId(out _)
        && DeskClaimsFactory.ReadExpiry(user) is { } expiry && now < expiry
        && ReadAuthTime(user) is { } authTime && now - authTime < AuthSetup.AbsoluteExpiry
        && user.FindFirstValue(AuthSetup.SessionIdClaim) is { Length: > 0 } sid && !revoked.IsRevoked(sid, now);

    public static DateTimeOffset? ReadAuthTime(ClaimsPrincipal user) =>
        DateTimeOffset.TryParse(user.FindFirstValue(AuthSetup.AuthTimeClaim), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
}

/// <summary>
/// Sessions ended by logout, in memory until the last token they could have issued is past the 24 h limit. Bearer
/// tokens are stateless, so this is what makes logout end the access token and the refresh token at once.
/// Limit (ADR-0024): the list is per process, so a restart or a second instance forgets it; a logged-out token
/// then lives until its own expiry (5 min access, 8 h refresh). Disabling or resetting the account ends it for good.
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
/// Single-use refresh tokens with reuse detection (#353, ADR-0024). Each token pair carries a refresh id
/// (<see cref="AuthSetup.RefreshIdClaim"/>); this holds, per session, the one id whose refresh token may still be
/// swapped. A refresh that presents any other id replays a token already swapped: a stolen copy, or the owner's own
/// after a thief swapped it first. The caller then ends the whole session, for both holders.
/// One exception, the grace window: for a short time after a swap, the id just swapped gets a pair for the current id
/// without a new swap. A page reload can abort a refresh after the server swapped the token, and the reloaded page
/// then sends the old token again; two racing refreshes with one token end up on one id the same way.
/// Per process, like <see cref="SessionRevocations"/>: after a restart a session's first refresh is believed and its
/// new id becomes the current one. Only ids and times are kept, never token values.
/// </summary>
public sealed class RefreshRotations(TimeSpan grace)
{
    /// <summary>The grace window when <see cref="GraceSecondsKey"/> is unset or not a positive number of seconds.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(30);
    /// <summary>The longest grace window a setting can ask for; a larger value is clamped to it.</summary>
    public static readonly TimeSpan MaxGrace = TimeSpan.FromSeconds(120);
    public const string GraceSecondsKey = "AUTH_REFRESH_REUSE_GRACE_SECONDS";

    private readonly ConcurrentDictionary<string, Entry> _current = new(StringComparer.Ordinal);

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
    public void Issue(string sid, string rid, DateTimeOffset until, DateTimeOffset now)
    {
        // Every sign-in prunes, so the map holds at most the sessions started in the last 24 h.
        foreach (var (key, entry) in _current)
            if (entry.Until <= now) _current.TryRemove(key, out _);
        _current[sid] = new Entry(rid, null, now, until);
    }

    /// <summary>
    /// The refresh id the new token pair carries, or null for a replay (the caller ends the session):
    /// <list type="bullet">
    /// <item>the current id is swapped for <paramref name="next"/>, which is returned;</item>
    /// <item>the id swapped last, within the grace window, gets the current id back and swaps nothing;</item>
    /// <item>any other id, or the last one after the window, is a replay.</item>
    /// </list>
    /// Of concurrent refreshes with one token, exactly one swaps; the others get the id it swapped to.
    /// </summary>
    public string? Rotate(string sid, string? presented, string next, DateTimeOffset until, DateTimeOffset now)
    {
        while (true)
        {
            if (!_current.TryGetValue(sid, out var entry))
            {
                if (_current.TryAdd(sid, new Entry(next, presented, now, until)))
                    return next;
                continue;
            }
            if (string.Equals(entry.Rid, presented, StringComparison.Ordinal))
            {
                if (_current.TryUpdate(sid, new Entry(next, presented, now, until), entry))
                    return next;
                continue; // another refresh swapped it first: look again, this id is now the previous one
            }
            return presented is not null && string.Equals(entry.Previous, presented, StringComparison.Ordinal) && now - entry.RotatedAt < grace
                ? entry.Rid
                : null;
        }
    }

    public int Count => _current.Count;

    /// <summary>The id that may be swapped, the one swapped for it (and when), and the session's 24 h end.</summary>
    private sealed record Entry(string Rid, string? Previous, DateTimeOffset RotatedAt, DateTimeOffset Until);
}
