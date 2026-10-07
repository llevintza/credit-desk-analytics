using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Desk.Api.Tests;

/// <summary>Mirrors the deploy pipeline's smoke checks (README §14.2), without a database.</summary>
public sealed class SmokeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Health_is_ok_and_reports_a_version_without_a_database()
    {
        var res = await factory.CreateClient().GetFromJsonAsync<HealthResponse>("/health", TestContext.Current.CancellationToken);
        Assert.Equal("ok", res!.Status);
        Assert.False(string.IsNullOrWhiteSpace(res.Version));
    }

    [Fact]
    public async Task Api_requires_authentication()
    {
        var res = await factory.CreateClient().GetAsync("/api/me", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task Unknown_api_route_is_404_not_the_spa()
    {
        var res = await factory.CreateClient().GetAsync("/api/nope", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task Client_routes_fall_back_to_the_spa()
    {
        var res = await factory.CreateClient().GetAsync("/positions", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("text/html", res.Content.Headers.ContentType?.MediaType);
    }
}
