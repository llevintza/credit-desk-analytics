using Desk.Api.Auth;
using Desk.Api.Positions;

namespace Desk.Api.Admin;

public static class AdminEndpoints
{
    public static RouteGroupBuilder MapAdminEndpoints(this RouteGroupBuilder api)
    {
        var admin = api.MapGroup("/admin").WithTags("Admin").RequireAuthorization(AuthSetup.AdminPolicy);
        admin.MapPost("/cache/clear", ClearCache)
            .WithName("ClearCache")
            .WithSummary("Drops cached reference data and grid blocks (after a manual reseed). Needs X-XSRF-TOKEN.")
            .Produces(StatusCodes.Status204NoContent);
        return api;
    }

    /// <summary>README §8: after a db-ops reseed the next request reloads the catalog, dates and data version.</summary>
    internal static IResult ClearCache(MetaCache meta, PositionsCache positions, ILogger<MetaCache> logger, HttpContext http)
    {
        meta.Invalidate();
        positions.Clear();
        logger.LogInformation("Caches cleared by {User}.", http.User.Identity!.Name);
        return Results.NoContent();
    }
}
