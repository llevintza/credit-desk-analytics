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
/// Per process, like <see cref="SessionRevocations"/>: after a restart a session's first refresh is believed and its
/// new id becomes the current one.
/// </summary>
public sealed class RefreshRotations
{
    private readonly ConcurrentDictionary<string, (string Rid, DateTimeOffset Until)> _current = new(StringComparer.Ordinal);

    /// <summary>A new sign-in: <paramref name="rid"/> is its current id until <paramref name="until"/> (its 24 h limit).</summary>
    public void Issue(string sid, string rid, DateTimeOffset until, DateTimeOffset now)
    {
        // Every sign-in prunes, so the map holds at most the sessions started in the last 24 h.
        foreach (var (key, entry) in _current)
            if (entry.Until <= now) _current.TryRemove(key, out _);
        _current[sid] = (rid, until);
    }

    /// <summary>
    /// Swaps <paramref name="presented"/> for <paramref name="next"/>. False when the session's current id is another
    /// one (reuse); of two concurrent refreshes with one token, exactly one wins.
    /// </summary>
    public bool TryRotate(string sid, string? presented, string next, DateTimeOffset until)
    {
        while (true)
        {
            if (!_current.TryGetValue(sid, out var entry))
            {
                if (_current.TryAdd(sid, (next, until)))
                    return true;
                continue;
            }
            if (!string.Equals(entry.Rid, presented, StringComparison.Ordinal))
                return false;
            if (_current.TryUpdate(sid, (next, until), entry))
                return true;
        }
    }

    public int Count => _current.Count;
}
