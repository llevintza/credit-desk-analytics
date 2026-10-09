using Desk.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Desk.Api.Hardening;

/// <summary>
/// ADR-0019 convention (#307): the statuses the shared pipeline can answer with are declared on every operation they
/// apply to, from the endpoint's own metadata, so no endpoint declares them by hand and none can forget them. An
/// endpoint declares only what its handler returns; a status both declare keeps the endpoint's entry.
/// </summary>
public sealed class PipelineResponses : IOpenApiOperationTransformer
{
    private const string ProblemJson = "application/problem+json";
    private static readonly HashSet<string> SafeMethods = new(["GET", "HEAD", "OPTIONS"], StringComparer.OrdinalIgnoreCase);

    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        // Every operation declares its success response, so Responses is never null here.
        var responses = operation.Responses!;
        var added = For(context.Description).Where(s => !responses.ContainsKey(s.Status)).ToList();
        if (added.Count == 0)
            return;
        // The same component the endpoints' ProducesProblem entries reference, not an inline copy per response.
        var document = context.Document!;
        document.AddComponent(nameof(ProblemDetails), await context.GetOrCreateSchemaAsync(typeof(ProblemDetails), null, cancellationToken));
        var problem = new OpenApiSchemaReference(nameof(ProblemDetails), document);
        foreach (var (status, description) in added)
            responses[status] = new OpenApiResponse
            {
                Description = description,
                Content = new Dictionary<string, OpenApiMediaType> { [ProblemJson] = new() { Schema = problem } },
            };
    }

    /// <summary>The pipeline statuses that apply to one operation, as (status code, description).</summary>
    internal static IEnumerable<(string Status, string Description)> For(ApiDescription api)
    {
        var metadata = api.ActionDescriptor.EndpointMetadata;
        // Maintenance mode and the rate limiter act on the /api path, not on endpoint metadata.
        var underApi = $"/{api.RelativePath}/".StartsWith("/api/", StringComparison.Ordinal);
        var authorize = metadata.OfType<IAuthorizeData>().ToList();
        var needsSession = authorize.Count > 0 && !metadata.OfType<IAllowAnonymous>().Any();
        var body = api.ParameterDescriptions.Any(p => p.Source == BindingSource.Body);
        var parsedQuery = api.ParameterDescriptions.Any(p => p.Source == BindingSource.Query && p.Type != typeof(string));
        var antiforgery = metadata.OfType<ValidatesAntiforgery>().Any() && !metadata.OfType<SkipAntiforgery>().Any()
            && !SafeMethods.Contains(api.HttpMethod!);

        var badRequest = new List<string>();
        if (antiforgery)
            badRequest.Add($"a missing or invalid {AuthSetup.AntiforgeryHeaderName} (problem type {AntiforgeryFilter.ProblemType})");
        if (body || parsedQuery)
            badRequest.Add("a request that doesn't bind");
        if (badRequest.Count > 0)
            yield return ("400", $"Bad request: {string.Join(", or ", badRequest)}.");
        if (needsSession)
            yield return ("401", "Not signed in, or the session has ended.");
        if (needsSession && authorize.Any(a => a.Policy is not null))
            yield return ("403", "Signed in without the role this operation's policy needs.");
        if (body)
            yield return ("415", "The request body isn't application/json.");
        if (underApi)
        {
            yield return ("429", "Rate limited; retry after the Retry-After header.");
            yield return ("503", "Maintenance mode (MAINTENANCE_MODE=true); retry after the Retry-After header.");
        }
    }
}
