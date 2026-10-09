using System.Text.Json;
using Desk.Data.Auth;

namespace Desk.Api.Tests;

/// <summary>
/// ADR-0019: each meta and preset operation documents every status its handler can return (#254). Statuses added by
/// the shared pipeline (401, 403, 429, the antiforgery 400, maintenance 503) aren't declared per endpoint.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class OpenApiResponsesTests(PostgresApiFactory api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("/api/meta/as-of", "get", "200,503")]
    [InlineData("/api/meta/columns", "get", "200")]
    [InlineData("/api/meta/portfolios", "get", "200")]
    [InlineData("/api/presets/{page}", "get", "200,404")]
    [InlineData("/api/presets/{page}", "put", "204,400,404,409")]
    [InlineData("/api/presets/{page}", "delete", "204,404")]
    public async Task Meta_and_preset_operations_document_every_status_their_handler_returns(string path, string method, string statuses)
    {
        var (admin, _, _) = await api.SignedInAsync(Roles.Admin);
        using var doc = JsonDocument.Parse(await admin.GetStringAsync("/openapi/v1.json", Ct));
        var responses = doc.RootElement.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("responses");
        Assert.Equal(statuses.Split(',').Order(), responses.EnumerateObject().Select(r => r.Name).Order());
    }
}
