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
/// so a list read before a save is never served after it.
/// </remarks>
public sealed class PresetListCache(PositionsCache cache)
{
    /// <summary>An idle user's list leaves the cache after this, so the entries track active sessions only.</summary>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(30);

    private readonly ConcurrentDictionary<(Guid User, string Page), CancellationTokenSource> _versions = new();

    public bool TryGet(Guid user, string page, out byte[]? bytes) => cache.Cache.TryGetValue(Key(user, page), out bytes);

    /// <summary>The current version of the user's list: take it before reading the database.</summary>
    public IChangeToken Version(Guid user, string page) =>
        new CancellationChangeToken(_versions.GetOrAdd((user, page), _ => new CancellationTokenSource()).Token);

    public void Store(Guid user, string page, byte[] bytes, IChangeToken version) =>
        cache.Cache.Set(Key(user, page), bytes,
            new MemoryCacheEntryOptions { Size = bytes.Length, SlidingExpiration = Idle }.AddExpirationToken(version));

    /// <summary>Ends the current version after a save or delete: the entry and any read in flight are dropped.</summary>
    public void Invalidate(Guid user, string page)
    {
        // Not disposed: a read in flight may still register on its token, and nothing here holds a timer.
        if (_versions.TryRemove((user, page), out var version))
            version.Cancel();
    }

    private static string Key(Guid user, string page) => $"presets:{user:N}:{page}";
}
