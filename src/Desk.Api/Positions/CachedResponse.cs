using Desk.Api.Audit;
using Microsoft.Extensions.Caching.Memory;

namespace Desk.Api.Positions;

/// <summary>
/// The cache-first read every data endpoint shares (README §7.2, §8): a weak ETag, <c>If-None-Match</c> → 304
/// without touching the database, serialized bytes from <see cref="PositionsCache"/> on a hit, and on a miss the
/// bytes stored until the next batch. Server-Timing, X-Cache and the audit cache flag are set either way.
/// </summary>
public static class CachedResponse
{
    /// <summary>Sets the ETag headers; returns the 304 or cached-bytes result, or null when the caller must compute.</summary>
    public static IResult? TryHit(HttpContext http, PositionsCache cache, string etag, string contentType, long started)
    {
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, no-cache";
        if (Matches(http.Request.Headers.IfNoneMatch, etag))
        {
            Mark(http, "HIT", 0, 0, started);
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }
        if (cache.Cache.TryGetValue(Key(etag), out byte[]? cached))
        {
            Mark(http, "HIT", 0, 0, started);
            return Results.Bytes(cached!, contentType); // only byte arrays are stored under these keys
        }
        return null;
    }

    /// <summary>Stores a computed response until the batch and returns it.</summary>
    public static IResult Store(HttpContext http, PositionsCache cache, string etag, byte[] bytes, string contentType,
        DateTimeOffset expires, double dbMs, double serializeMs, long started, int rows)
    {
        cache.Cache.Set(Key(etag), bytes, new MemoryCacheEntryOptions { Size = bytes.Length, AbsoluteExpiration = expires });
        Mark(http, "MISS", dbMs, serializeMs, started);
        if (http.Features.Get<AuditFeature>() is { } audit) audit.Rows = rows;
        return Results.Bytes(bytes, contentType);
    }

    /// <summary><c>If-None-Match</c> may list several tags (comma-separated, possibly across header lines) or <c>*</c>.</summary>
    internal static bool Matches(Microsoft.Extensions.Primitives.StringValues header, string etag) =>
        header.ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries) // joins multiple lines with ','
            .Any(tag => tag == etag || tag == "*");

    private static string Key(string etag) => $"resp:{etag}";

    private static void Mark(HttpContext http, string cache, double dbMs, double serializeMs, long started)
    {
        PositionsEndpoints.SetTiming(http, cache, dbMs, serializeMs, started);
        if (http.Features.Get<AuditFeature>() is { } audit) audit.Cache = cache;
    }
}
