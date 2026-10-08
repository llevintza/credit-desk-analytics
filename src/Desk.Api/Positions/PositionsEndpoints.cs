using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Desk.Api.Audit;
using Desk.Api.Limits;
using Desk.Data.Catalog;
using Desk.Data.Grid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Net.Http.Headers;

namespace Desk.Api.Positions;

/// <summary>P1 positions grid (README §6): one block + summary per request, cache-first, and a streamed CSV export.</summary>
public static class PositionsEndpoints
{
    public const int MaxExportRows = 25_000;

    public static RouteGroupBuilder MapPositionsEndpoints(this RouteGroupBuilder api)
    {
        var positions = api.MapGroup("/positions").WithTags("Positions");

        positions.MapPost("/query", QueryAsync)
            .WithName("QueryPositions")
            .WithSummary("One block of the positions grid (columnar) plus the summary over all filtered rows. JSON, or MessagePack on Accept: application/x-msgpack.")
            .Produces(StatusCodes.Status200OK, contentType: ColumnarSerializer.JsonContentType)
            .Produces(StatusCodes.Status304NotModified)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        positions.MapPost("/export", ExportAsync)
            .WithMetadata(ExportEndpoint.Instance)
            .WithName("ExportPositions")
            .WithSummary($"Streams the filtered positions as CSV (displayed columns, at most {MaxExportRows:N0} rows; one export at a time per user, a few in total, under an overall deadline).")
            .Produces(StatusCodes.Status200OK, contentType: "text/csv")
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    internal static async Task<IResult> QueryAsync(
        GridRequest request, HttpContext http, MetaCache metaCache, IPortfolioEntitlements entitlements,
        GridRepository grid, PositionsCache cache, TimeProvider time, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var meta = await metaCache.GetAsync(ct);
        if (Resolve(request, meta, entitlements, http) is not { } resolved)
            return Unavailable(meta, request);
        if (resolved.Error is not null)
            return resolved.Error;
        var query = resolved.Query!;

        var msgpack = WantsMsgPack(http.Request);
        var contentType = msgpack ? ColumnarSerializer.MsgPackContentType : ColumnarSerializer.JsonContentType;
        var hash = Hash(query.CanonicalKey);
        // The ETag changes with the data (reseed), the query and the representation: a 304 needs no database read.
        var etag = $"W/\"{query.AsOf:yyyy-MM-dd}:{meta.DataVersion}:{hash}:{(msgpack ? "mp" : "js")}\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "private, no-cache";
        http.Response.Headers.Vary = HeaderNames.Accept;
        var audit = http.Features.Get<AuditFeature>();

        if (http.Request.Headers.IfNoneMatch.Contains(etag))
        {
            SetTiming(http, "HIT", 0, 0, started);
            if (audit is not null) audit.Cache = "HIT";
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        var key = $"pos:{etag}";
        if (cache.Cache.TryGetValue(key, out byte[]? cached) && cached is not null)
        {
            SetTiming(http, "HIT", 0, 0, started);
            if (audit is not null) audit.Cache = "HIT";
            return Results.Bytes(cached, contentType);
        }

        // Totals depend on the filter, not on paging or sort: the first block of a view computes them (one round
        // trip with the page) and every later block of that view reads only its indexed page.
        var summaryKey = $"sum:{query.AsOf:yyyy-MM-dd}:{meta.DataVersion}:{Hash(query.SummaryKey)}";
        cache.Cache.TryGetValue(summaryKey, out GridSummary? summary);
        var block = await grid.ReadBlockAsync(query, meta.Catalog, ct, summary);
        // MemoryCache keeps its own system clock, so an absolute time from the app's TimeProvider would be compared
        // on another clock: give it the time left until the batch instead (at least a tick, which the API requires).
        var untilBatch = TimeSpan.FromTicks(Math.Max(1, (meta.BatchEndsAt - time.GetUtcNow()).Ticks));
        if (summary is null)
            cache.Cache.Set(summaryKey, new GridSummary(block.RowCount, block.Summary),
                new MemoryCacheEntryOptions { Size = 256 + 64 * block.Summary.Count, AbsoluteExpirationRelativeToNow = untilBatch });
        var serializeStarted = Stopwatch.GetTimestamp();
        var generatedAt = time.GetUtcNow();
        var bytes = msgpack
            ? ColumnarSerializer.ToMsgPack(block, query.AsOf, generatedAt)
            : ColumnarSerializer.ToJson(block, query.AsOf, generatedAt);
        var serializeMs = Stopwatch.GetElapsedTime(serializeStarted).TotalMilliseconds;

        cache.Cache.Set(key, bytes, new MemoryCacheEntryOptions { Size = bytes.Length, AbsoluteExpirationRelativeToNow = untilBatch });
        SetTiming(http, "MISS", block.DbMs, serializeMs, started);
        if (audit is not null)
        {
            audit.Cache = "MISS";
            audit.Rows = block.Rows;
        }
        return Results.Bytes(bytes, contentType);
    }

    internal static async Task<IResult> ExportAsync(
        GridRequest request, HttpContext http, MetaCache metaCache, IPortfolioEntitlements entitlements,
        GridRepository grid, ExportGate gate, LimitsOptions limits, ILoggerFactory loggers, CancellationToken ct)
    {
        var meta = await metaCache.GetAsync(ct);
        if (Resolve(request, meta, entitlements, http) is not { } resolved)
            return Unavailable(meta, request);
        if (resolved.Error is not null)
            return resolved.Error;
        var query = resolved.Query!;

        // The overall deadline: a slow or stalled reader can't hold the permit and the connection for longer. Armed
        // before a slot is taken, so nothing can throw between taking the slots and the finally that frees them.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(limits.ExportTimeout);

        // One export at a time per user, and a few in total (README §7.2, #127): an export holds a database permit
        // and a connection for the whole stream.
        var user = http.User.Identity!.Name!;
        switch (gate.TryBegin(user))
        {
            case ExportGate.Result.UserBusy:
                http.Response.Headers.RetryAfter = "5";
                return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Export already running",
                    detail: "Wait for your current export to finish.");
            case ExportGate.Result.Full:
                http.Response.Headers.RetryAfter = "10";
                return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many exports",
                    detail: "Other exports are running. Try again shortly.");
        }

        try
        {
            var rows = await WriteCsvAsync(http, grid, query, meta, deadline.Token);
            if (http.Features.Get<AuditFeature>() is { } audit) audit.Rows = rows;
            return Results.Empty;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            loggers.CreateLogger(typeof(PositionsEndpoints)).LogWarning(
                "Export for {User} stopped at its {Seconds} s deadline", user, limits.ExportTimeout.TotalSeconds);
            if (!http.Response.HasStarted)
            {
                // Not a file any more. No Retry-After: the same export would hit the same deadline.
                http.Response.Headers.Remove(HeaderNames.ContentDisposition);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Export timed out",
                    detail: "Narrow the filter or the columns and try again.");
            }
            // Part of the file is already sent: break the connection so the download fails instead of ending as a
            // CSV that looks complete.
            http.Abort();
            return Results.Empty;
        }
        finally
        {
            // Also on client cancel: the stream's connection is disposed by the time the exception gets here.
            gate.End(user);
        }
    }

    private static async Task<int> WriteCsvAsync(HttpContext http, GridRepository grid, GridQuery query, MetaSnapshot meta, CancellationToken ct)
    {
        var export = query with { Offset = 0, Limit = MaxExportRows };
        http.Response.ContentType = "text/csv; charset=utf-8";
        http.Response.Headers.ContentDisposition = $"attachment; filename=\"positions-{query.AsOf:yyyy-MM-dd}.csv\"";
        // Not `await using`: disposing flushes, and a flush must not run after a deadline or a client abort.
        var writer = new StreamWriter(http.Response.Body, new UTF8Encoding(false), bufferSize: 64 * 1024);
        await writer.WriteLineAsync(string.Join(',', export.Columns.Select(c => Csv.Escape(c.Header))).AsMemory(), ct);

        var rows = 0;
        var line = new StringBuilder(1024);
        await foreach (var reader in grid.StreamAsync(export, meta.Catalog, ct))
        {
            line.Clear();
            for (var i = 0; i < export.Columns.Count; i++)
            {
                if (i > 0) line.Append(',');
                Csv.Append(line, export.Columns[i], reader, i);
            }
            await writer.WriteLineAsync(line, ct);
            rows++;
        }
        await writer.FlushAsync(ct);
        return rows;
    }

    private sealed record Resolved(GridQuery? Query, IResult? Error);

    /// <summary>
    /// Validates the as-of date and whitelists the request. Null when there's no data or the date is unknown; an
    /// error result when a filter is too large to apply (dropping it would widen the result).
    /// </summary>
    private static Resolved? Resolve(GridRequest request, MetaSnapshot meta, IPortfolioEntitlements entitlements, HttpContext http)
    {
        if (!meta.HasData) return null;
        var asOf = request.AsOf ?? meta.AsOfDates[0];
        if (!meta.AsOfDates.Contains(asOf)) return null;
        try
        {
            return new Resolved(meta.Normalizer!.Normalize(request, asOf, entitlements.For(http.User, meta)), null);
        }
        catch (GridRequestException e)
        {
            return new Resolved(null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Filter too large", detail: e.Message));
        }
    }

    private static IResult Unavailable(MetaSnapshot meta, GridRequest request) => meta.HasData
        ? Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Unknown as-of date",
            detail: $"No positions for {request.AsOf:yyyy-MM-dd}. See GET /api/meta/as-of.")
        : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No data loaded",
            detail: "The database has not been seeded yet.");

    internal static bool WantsMsgPack(HttpRequest request) =>
        request.Headers.Accept.ToString().Contains(ColumnarSerializer.MsgPackContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>README §8 observability: <c>Server-Timing</c> (db, serialize, total) and <c>X-Cache</c>.</summary>
    internal static void SetTiming(HttpContext http, string cache, double dbMs, double serializeMs, long started)
    {
        var total = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        http.Response.Headers["X-Cache"] = cache;
        http.Response.Headers["Server-Timing"] = string.Create(CultureInfo.InvariantCulture,
            $"db;dur={dbMs:0.0}, ser;dur={serializeMs:0.0}, total;dur={total:0.0}");
    }

    internal static string Hash(string canonical) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..20];
}

/// <summary>CSV values formatted by catalog kind, in display units (the same precision the grid shows).</summary>
internal static class Csv
{
    /// <summary>
    /// RFC 4180 quoting, plus formula-injection defence: a text cell starting with = + - @ (or tab/CR) would run as
    /// a formula when the file is opened in a spreadsheet, so it gets a leading apostrophe (OWASP CSV injection).
    /// </summary>
    public static string Escape(string s)
    {
        if (s.Length > 0 && s[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            s = "'" + s;
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }

    public static void Append(StringBuilder line, ColumnDef col, System.Data.Common.DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return;
        var inv = CultureInfo.InvariantCulture;
        switch (col.Kind)
        {
            case ColumnKind.Text: line.Append(Escape(r.GetString(i))); break;
            case ColumnKind.Date: line.Append(r.GetFieldValue<DateOnly>(i).ToString("yyyy-MM-dd", inv)); break;
            case ColumnKind.Flag: line.Append(r.GetBoolean(i) ? "true" : "false"); break;
            case ColumnKind.Money: line.Append(r.GetDecimal(i).ToString("0.00", inv)); break;
            case ColumnKind.Key when col.Name == GridQueryNormalizer.RowIdColumn: line.Append(r.GetInt64(i).ToString(inv)); break;
            case ColumnKind.Key or ColumnKind.Count: line.Append(r.GetInt32(i).ToString(inv)); break;
            case ColumnKind.Price: line.Append(r.GetDouble(i).ToString("0.000", inv)); break;
            case ColumnKind.Bp: line.Append(r.GetDouble(i).ToString("0", inv)); break;
            default: line.Append(r.GetDouble(i).ToString("0.00####", inv)); break; // Pct (ratio) and Ratio keep their precision
        }
    }
}
