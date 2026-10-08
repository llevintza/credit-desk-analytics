using Microsoft.AspNetCore.Antiforgery;

namespace Desk.Api.Auth;

/// <summary>
/// Validates the antiforgery token on state-changing API calls (README §7.1). The built-in
/// <c>UseAntiforgery</c> only covers form posts; the SPA sends JSON with the token in <c>X-XSRF-TOKEN</c>.
/// Login is exempt (<see cref="SkipAntiforgery"/>): it's JSON-only (a cross-site form can't send that without
/// CORS), it's rate limited per IP, and there is no session to ride yet. See ADR-0005.
/// </summary>
public sealed class AntiforgeryFilter(IAntiforgery antiforgery) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        var http = ctx.HttpContext;
        if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
            && !HttpMethods.IsOptions(http.Request.Method)
            && http.GetEndpoint()?.Metadata.GetMetadata<SkipAntiforgery>() is null
            && !await antiforgery.IsRequestValidAsync(http))
        {
            return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Missing or invalid antiforgery token",
                detail: $"Send the XSRF-TOKEN cookie value in the {AuthSetup.AntiforgeryHeaderName} header.");
        }
        return await next(ctx);
    }
}

/// <summary>Endpoint metadata: this endpoint doesn't need an antiforgery token.</summary>
public sealed class SkipAntiforgery
{
    public static readonly SkipAntiforgery Instance = new();
}
