using System.Collections.Concurrent;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Primitives;

namespace Desk.Api.Positions;

/// <summary>
/// A user's preset list for a page, serialized, in <see cref="PositionsCache"/> (README §7.2 cache-first, ADR-0014).
/// The positions page asks for it on every view; uncached, each revisit woke Neon for a list that changes only when
/// its owner saves or deletes one. A save or delete drops that user's entry on this instance (the app runs as one).
/// </summary>
/// <remarks>
/// Each (user, page) has a version: a read takes it before going to the database and stores its result under it, and
/// a save or delete ends it. A read that raced a write stores under an ended version, which the cache drops at once,
/// so a list read before a save is never served after it. A version also ends when its entry leaves the cache (idle,
/// evicted for space, or cleared), and all of them end if <see cref="MaxVersions"/> pile up (reads that failed before
/// storing), so the versions track the cached lists instead of every (user, page) since start (#67). Ending a version
/// early is always safe: it costs the next read a MISS, never a stale list.
/// </remarks>
public sealed class PresetListCache(PositionsCache cache)
{
    /// <summary>An idle user's list leaves the cache after this, so the entries track active sessions only.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    /// <summary>A backstop far above the demo's users × pages: past it, every version ends at once.</summary>
    public const int MaxVersions = 10_000;

    private readonly ConcurrentDictionary<(Guid User, string Page), CancellationTokenSource> _versions = new();

    /// <summary>The versions held now, for tests.</summary>
    internal int Versions => _versions.Count;

    public bool TryGet(Guid user, string page, out byte[]? bytes) => cache.Cache.TryGetValue(Key(user, page), out bytes);

    /// <summary>The current version of the user's list: take it before reading the database.</summary>
    public IChangeToken Version(Guid user, string page)
    {
        if (!_versions.TryGetValue((user, page), out var version))
        {
            if (_versions.Count >= MaxVersions)
                foreach (var key in _versions.Keys) End(key);
            version = _versions.GetOrAdd((user, page), _ => new CancellationTokenSource());
        }
        return new CancellationChangeToken(version.Token);
    }

    public void Store(Guid user, string page, byte[] bytes, IChangeToken version) =>
        cache.Cache.Set(Key(user, page), bytes,
            new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = Idle }.AddExpirationToken(version)
                .RegisterPostEvictionCallback(Evicted, (user, page)));

    /// <summary>Ends the current version after a save or delete: the entry and any read in flight are dropped.</summary>
    public void Invalidate(Guid user, string page) => End((user, page));

    private void End((Guid User, string Page) key)
    {
        // Not disposed: a read in flight may still register on its token, and nothing here holds a timer.
        if (_versions.TryRemove(key, out var version))
            version.Cancel();
    }

    /// <summary>
    /// The entry left the cache: end its version with it. A replaced entry's version lives on in its successor, and an
    /// ended one (a save or delete) is already gone.
    /// </summary>
    private void Evicted(object key, object? value, EvictionReason reason, object? state)
    {
        if (reason is not (EvictionReason.Replaced or EvictionReason.TokenExpired))
            End(((Guid, string))state!);
    }

    private static string Key(Guid user, string page) => $"presets:{user:N}:{page}";
}
