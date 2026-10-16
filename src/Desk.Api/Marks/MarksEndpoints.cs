using System.Diagnostics;
using System.Globalization;
using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Desk.Api.Audit;
using Desk.Api.Insights;
using Desk.Api.Limits;
using Desk.Api.Positions;
using Desk.Data.Grid;
using Desk.Data.Marks;
using Microsoft.Extensions.Caching.Memory;

namespace Desk.Api.Marks;

/// <summary>The first event of a stream: what it marks, how often, and when the server will end it.</summary>
public sealed record MarkHello(DateOnly AsOf, int Bonds, int TickMs, DateTimeOffset EndsAt, long Seq);

/// <summary>The last event of a stream: <c>lifetime</c> (reconnect if the user is still there) or <c>empty</c> (nothing to mark: don't).</summary>
public sealed record MarkBye(string Reason);

/// <summary>The overlay totals of one view at tick <paramref name="Seq"/>: the marked columns' weighted averages with every mark applied.</summary>
public sealed record MarkTotalsResponse(DateOnly AsOf, long Seq, DateTimeOffset EventTs, int? RowCount, Dictionary<string, double?> Summary);

/// <summary>The marked bonds of one portfolio scope on the latest as-of date.</summary>
public sealed record MarkUniverse(DateOnly AsOf, IReadOnlyList<MarkedBond> Bonds);

/// <summary>
/// The universe per (as-of, data version, portfolio set), cache-first until the next batch (README §7.2): a reconnect
/// or a totals refresh reads nothing from the database.
/// </summary>
public sealed class MarkUniverses(MarkRepository repo, PositionsCache cache, MarksOptions options)
{
    public async Task<MarkUniverse> GetAsync(MetaSnapshot meta, IReadOnlyCollection<int> portfolios, CancellationToken ct)
    {
        var asOf = meta.AsOfDates[0];
        int[] scope = [.. portfolios.Distinct().Order()];
        var key = $"marks:{asOf:yyyy-MM-dd}:{meta.DataVersion}:{string.Join(',', scope)}";
        if (cache.Cache.TryGetValue(key, out MarkUniverse? hit))
            return hit!;
        var universe = new MarkUniverse(asOf, await repo.UniverseAsync(asOf, scope, options.MaxBonds, ct));
        cache.Cache.Set(key, universe, new MemoryCacheEntryOptions { Size = 128 + 32 * universe.Bonds.Count, AbsoluteExpiration = meta.BatchEndsAt });
        return universe;
    }
}

/// <summary>
/// P6 intraday overlay (README §6 P6, ADR-0015): a Server-Sent Events stream of simulated marks for a subset of the
/// caller's bonds, and the overlay totals the grid refreshes periodically. Both are scoped to the caller's
/// portfolios (ADR-0021). The stream is read with <c>fetch</c>, not <c>EventSource</c>, so it carries the bearer
/// token like every other call (ADR-0024).
/// </summary>
public static class MarksEndpoints
{
    /// <summary>How long a client waits before reconnecting after a dropped stream (the SSE <c>retry</c> field).</summary>
    public static readonly TimeSpan Retry = TimeSpan.FromSeconds(5);

    public static RouteGroupBuilder MapMarksEndpoints(this RouteGroupBuilder api)
    {
        var marks = api.MapGroup("/marks").WithTags("Marks");

        marks.MapGet("/stream", StreamAsync)
            .WithMetadata(MarkStreamEndpoint.Instance)
            .WithName("StreamMarks")
            .WithSummary("Server-Sent Events: simulated intraday marks (bond_id, seq, event_ts, price, spread) for a subset of the caller's bonds "
                + "on the latest as-of date. Events: hello, mark (id = seq), bye. Last-Event-ID (header, or the lastEventId parameter) replays only "
                + "marks newer than it. The server "
                + "ends each stream after MARKS_STREAM_SECONDS (at most 5 minutes).")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        marks.MapPost("/totals", TotalsAsync)
            .WithName("MarkTotals")
            .WithSummary("The positions view's price and spread totals (|market value|-weighted) with the intraday marks applied, as of the "
                + "start of the current MARKS_TOTALS_SECONDS window. Same body as POST /api/positions/query; latest as-of only.")
            .Produces<MarkTotalsResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return api;
    }

    internal static async Task<IResult> StreamAsync(
        string? portfolioIds, string? lastEventId, HttpContext http, MetaCache metaCache, IPortfolioEntitlements entitlements, MarkUniverses universes,
        DatabasePermits permits, MarksOptions options, TimeProvider time, CancellationToken ct)
    {
        if (!InsightsEndpoints.TryParseIds(portfolioIds, out var requested))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid portfolioIds",
                detail: "portfolioIds is a comma-separated list of integers.");

        // The stream holds no database permit while it streams (RateLimiting): its one read, the meta snapshot and
        // the universe (both cache-first), takes a permit here and gives it back before the first event.
        MarkUniverse universe;
        using (var lease = await permits.AcquireAsync(ct))
        {
            if (!lease.IsAcquired)
            {
                http.Response.Headers.RetryAfter = "1";
                return Results.Problem(statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests",
                    detail: "Slow down and retry in 1 s.");
            }
            var meta = await metaCache.GetAsync(ct);
            if (!meta.HasData)
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No data loaded",
                    detail: "The database has not been seeded yet.");
            // Entitlements first (ADR-0021): requested ids outside the caller's set are dropped; none requested = all of it.
            var entitled = entitlements.For(http.User, meta);
            universe = await universes.GetAsync(meta, requested.Length > 0 ? [.. requested.Where(entitled.Contains)] : entitled, ct);
        }

        // EventSource sends the header; the SPA's fetch sends the query parameter instead, because a cross-origin
        // Last-Event-ID header would need its own CORS preflight allowance (ADR-0024 keeps that list short).
        var resume = http.Request.Headers["Last-Event-ID"].FirstOrDefault() ?? lastEventId;
        var after = long.TryParse(resume, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) ? id : long.MinValue;
        // Proxies that buffer responses (nginx-style) must pass each event straight through.
        http.Response.Headers["X-Accel-Buffering"] = "no";
        return TypedResults.ServerSentEvents(Events(universe, after, options, time, http.Features.Get<AuditFeature>(), ct));
    }

    /// <summary>
    /// hello; then, every tick, the marks that changed since the last one sent (on connect: every bond's current mark
    /// newer than Last-Event-ID); then bye when the lifetime is up. Waits on the app's <see cref="TimeProvider"/>.
    /// </summary>
    internal static async IAsyncEnumerable<SseItem<string>> Events(
        MarkUniverse universe, long after, MarksOptions options, TimeProvider time, AuditFeature? audit,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var sim = new MarkSimulator(options.Tick);
        var endsAt = time.GetUtcNow() + options.Lifetime;
        var now = sim.TickAt(time.GetUtcNow());
        yield return new SseItem<string>(
            Json(new MarkHello(universe.AsOf, universe.Bonds.Count, (int)options.Tick.TotalMilliseconds, endsAt, now), DeskJsonContext.Default.MarkHello),
            "hello") { ReconnectionInterval = Retry };
        if (universe.Bonds.Count == 0)
        {
            yield return Bye("empty");
            yield break;
        }

        var sent = 0;
        while (true)
        {
            foreach (var mark in sim.ChangedBetween(universe.Bonds, after, now))
            {
                sent++;
                yield return new SseItem<string>(Json(mark, DeskJsonContext.Default.Mark), "mark") { EventId = mark.Seq.ToString(CultureInfo.InvariantCulture) };
            }
            if (audit is not null) audit.Rows = sent;
            after = Math.Max(after, now);

            var wait = Min(sim.TimeOf(now + 1), endsAt) - time.GetUtcNow();
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait, time, ct);
            if (time.GetUtcNow() >= endsAt)
            {
                yield return Bye("lifetime");
                yield break;
            }
            now = sim.TickAt(time.GetUtcNow());
        }
    }

    internal static async Task<IResult> TotalsAsync(
        GridRequest request, HttpContext http, MetaCache metaCache, IPortfolioEntitlements entitlements, MarkUniverses universes,
        MarkRepository marks, PositionsCache cache, MarksOptions options, TimeProvider time, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var meta = await metaCache.GetAsync(ct);
        if (PositionsEndpoints.Resolve(request, meta, entitlements, http) is not { } resolved)
            return PositionsEndpoints.Unavailable(meta, request);
        if (resolved.Error is not null)
            return resolved.Error;
        var query = resolved.Query!;
        if (query.AsOf != meta.AsOfDates[0])
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "No intraday marks",
                detail: $"Intraday marks apply to the latest as-of date ({meta.AsOfDates[0]:yyyy-MM-dd}) only.");

        // One computation per view per window, shared by every caller with the same scope and filter: the window's
        // start is the tick the totals are as of (its seq), and the entry expires when the next window starts.
        var sim = new MarkSimulator(options.Tick);
        var windowTicks = Math.Max(1, (long)(options.TotalsBucket / options.Tick));
        var tick = sim.TickAt(time.GetUtcNow());
        var at = tick - tick % windowTicks;
        // Nothing on screen to overlay: nothing to read.
        if (!query.Columns.Any(c => MarkRepository.MarkedColumns.Contains(c.Name)))
            return TypedResults.Ok(new MarkTotalsResponse(query.AsOf, at, sim.TimeOf(at), null, []));
        var key = $"mktot:{query.AsOf:yyyy-MM-dd}:{meta.DataVersion}:{PositionsEndpoints.Hash(query.SummaryKey)}:{at}";
        if (cache.Cache.TryGetValue(key, out MarkTotalsResponse? hit))
        {
            PositionsEndpoints.SetTiming(http, "HIT", 0, 0, started);
            return TypedResults.Ok(hit);
        }

        var dbStarted = Stopwatch.GetTimestamp();
        var universe = await universes.GetAsync(meta, query.PortfolioIds, ct);
        var totals = await marks.TotalsAsync(query, meta.Catalog, [.. universe.Bonds.Select(b => sim.MarkAt(b, at))], ct);
        var dbMs = Stopwatch.GetElapsedTime(dbStarted).TotalMilliseconds;
        var response = new MarkTotalsResponse(query.AsOf, at, sim.TimeOf(at), totals?.RowCount,
            totals?.Values.ToDictionary(v => v.Column.Name, v => (double?)v.Value) ?? []);
        cache.Cache.Set(key, response, new MemoryCacheEntryOptions { Size = 256, AbsoluteExpiration = sim.TimeOf(at + windowTicks) });
        PositionsEndpoints.SetTiming(http, "MISS", dbMs, 0, started);
        if (http.Features.Get<AuditFeature>() is { } audit) audit.Rows = totals?.RowCount ?? 0;
        return TypedResults.Ok(response);
    }

    private static SseItem<string> Bye(string reason) => new(Json(new MarkBye(reason), DeskJsonContext.Default.MarkBye), "bye");

    private static string Json<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) => JsonSerializer.Serialize(value, type);

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}
