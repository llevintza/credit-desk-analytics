using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Desk.Data.Grid;

namespace Desk.Api.Positions;

public sealed record AsOfResponse(DateOnly Latest, DateOnly[] Dates);

public sealed record CatalogColumn(string Name, string Group, string Kind, string Aggregation, string Header);

public sealed record PortfolioResponse(int PortfolioId, string Name, int FundId, string FundName);

public sealed record PresetResponse(string Name, bool BuiltIn, JsonElement State, DateTimeOffset? UpdatedAt);

public sealed record SavePresetRequest(string? Name, JsonElement State);

/// <summary>README §8 meta endpoints (served from the per-batch <see cref="MetaCache"/>) and per-user column presets.</summary>
public static class MetaEndpoints
{
    public const int MaxPresetStateBytes = 64 * 1024;
    public static readonly IReadOnlySet<string> Pages = new HashSet<string>(StringComparer.Ordinal) { BuiltInPresets.Page };

    public static RouteGroupBuilder MapMetaEndpoints(this RouteGroupBuilder api)
    {
        var meta = api.MapGroup("/meta").WithTags("Meta");
        meta.MapGet("/as-of", AsOfAsync)
            .WithName("GetAsOfDates").WithSummary("The snapshot's as-of dates, newest first.")
            .Produces<AsOfResponse>().ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        meta.MapGet("/columns", ColumnsAsync)
            .WithName("GetColumnCatalog").WithSummary("The column catalog (name, group, kind, aggregation, header) that drives the grid.")
            .Produces<CatalogColumn[]>();
        meta.MapGet("/portfolios", PortfoliosAsync)
            .WithName("GetPortfolios").WithSummary("The portfolios the current user is entitled to.")
            .Produces<PortfolioResponse[]>();

        var presets = api.MapGroup("/presets").WithTags("Presets");
        presets.MapGet("/{page}", ListPresetsAsync)
            .WithName("ListPresets").WithSummary("Built-in presets plus the current user's saved presets for a page.")
            .Produces<PresetResponse[]>().ProducesProblem(StatusCodes.Status404NotFound);
        presets.MapPut("/{page}", SavePresetAsync)
            .WithName("SavePreset").WithSummary("Creates or replaces one of the current user's presets. Needs X-XSRF-TOKEN.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status400BadRequest).ProducesProblem(StatusCodes.Status404NotFound);
        presets.MapDelete("/{page}", DeletePresetAsync)
            .WithName("DeletePreset").WithSummary("Deletes one of the current user's presets (?name=). Needs X-XSRF-TOKEN.")
            .Produces(StatusCodes.Status204NoContent).ProducesProblem(StatusCodes.Status404NotFound);
        return api;
    }

    internal static async Task<IResult> AsOfAsync(HttpContext http, MetaCache cache, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var meta = await cache.GetAsync(ct);
        PositionsEndpoints.SetTiming(http, "HIT", 0, 0, started);
        return meta.AsOfDates.Count == 0
            ? Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "No data loaded")
            : Results.Ok(new AsOfResponse(meta.AsOfDates[0], [.. meta.AsOfDates]));
    }

    internal static async Task<IResult> ColumnsAsync(HttpContext http, MetaCache cache, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var meta = await cache.GetAsync(ct);
        PositionsEndpoints.SetTiming(http, "HIT", 0, 0, started);
        return Results.Ok(meta.Catalog.Select(c => new CatalogColumn(c.Name, c.Group, c.Kind.ToString(), c.Aggregation.ToString(), c.Header)).ToArray());
    }

    internal static async Task<IResult> PortfoliosAsync(HttpContext http, MetaCache cache, IPortfolioEntitlements entitlements, CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var meta = await cache.GetAsync(ct);
        var allowed = entitlements.For(http.User, meta);
        PositionsEndpoints.SetTiming(http, "HIT", 0, 0, started);
        return Results.Ok(meta.Portfolios.Where(p => allowed.Contains(p.PortfolioId))
            .Select(p => new PortfolioResponse(p.PortfolioId, p.Name, p.FundId, p.FundName)).ToArray());
    }

    internal static async Task<IResult> ListPresetsAsync(string page, HttpContext http, PresetRepository presets, CancellationToken ct)
    {
        if (!Pages.Contains(page)) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown page");
        var builtIn = BuiltInPresets.ByName.Select(p => new PresetResponse(p.Key, true,
            JsonSerializer.SerializeToElement(new BuiltInState(p.Value), DeskJsonContext.Default.BuiltInState), null));
        var own = (await presets.ListAsync(UserId(http.User), page, ct))
            .Select(p => new PresetResponse(p.Name, false, JsonDocument.Parse(p.State).RootElement.Clone(), p.UpdatedAt));
        return Results.Ok(builtIn.Concat(own).ToArray());
    }

    internal static async Task<IResult> SavePresetAsync(string page, SavePresetRequest body, HttpContext http, PresetRepository presets, CancellationToken ct)
    {
        if (!Pages.Contains(page)) return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Unknown page");
        var name = body.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 64 || BuiltInPresets.ByName.ContainsKey(name))
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid preset name",
                detail: "Use 1-64 characters, not the name of a built-in preset.");
        var state = body.State.ValueKind == JsonValueKind.Object ? body.State.GetRawText() : null;
        if (state is null || state.Length > MaxPresetStateBytes)
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Invalid preset state",
                detail: $"State must be a JSON object of at most {MaxPresetStateBytes / 1024} KB.");

        return await presets.SaveAsync(UserId(http.User), page, name, state, ct)
            ? Results.NoContent()
            : Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Too many presets",
                detail: $"Delete one first: at most {PresetRepository.MaxPresetsPerPage} per page.");
    }

    internal static async Task<IResult> DeletePresetAsync(string page, string? name, HttpContext http, PresetRepository presets, CancellationToken ct) =>
        Pages.Contains(page) && name is not null && await presets.DeleteAsync(UserId(http.User), page, name, ct)
            ? Results.NoContent()
            : Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "No such preset");

    private static Guid UserId(ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(ClaimTypes.NameIdentifier)!);
}

public sealed record BuiltInState(IReadOnlyList<string> Columns);
