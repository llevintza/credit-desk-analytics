using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Desk.Api.Tests;

public sealed class OpenApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task OpenApi_document_lists_the_api_endpoints()
    {
        var res = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var paths = doc.RootElement.GetProperty("paths");
        Assert.True(paths.TryGetProperty("/health", out _));
        Assert.True(paths.TryGetProperty("/api/me", out _));
        // Exactly those two: SPA and /api fallbacks must stay excluded.
        Assert.Equal(2, paths.EnumerateObject().Count());
        Assert.Equal("Credit Desk Analytics API", doc.RootElement.GetProperty("info").GetProperty("title").GetString());
    }

    [Fact]
    public async Task OpenApi_document_has_no_absolute_http_server()
    {
        var res = await factory.CreateClient().GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        if (!doc.RootElement.TryGetProperty("servers", out var servers)
            || servers.ValueKind != JsonValueKind.Array
            || servers.GetArrayLength() == 0)
        {
            return;
        }

        foreach (var server in servers.EnumerateArray())
        {
            var url = server.GetProperty("url").GetString();
            Assert.False(
                url is not null && url.StartsWith("http://", StringComparison.OrdinalIgnoreCase),
                $"OpenAPI server URL must not be an absolute http:// entry (got '{url}').");
            Assert.True(
                url is not null && (url.StartsWith('/') || !Uri.TryCreate(url, UriKind.Absolute, out _)),
                $"OpenAPI server URL must be relative (got '{url}').");
        }
    }

    [Fact]
    public async Task Swagger_ui_is_served_not_the_spa()
    {
        var res = await factory.CreateClient().GetAsync("/swagger/index.html", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("swagger-ui", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Swagger_redirects_to_index_html()
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var res = await client.GetAsync("/swagger", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.MovedPermanently, res.StatusCode);
        Assert.NotNull(res.Headers.Location);
        var path = res.Headers.Location.IsAbsoluteUri
            ? res.Headers.Location.AbsolutePath
            : "/" + res.Headers.Location.OriginalString.TrimStart('/');
        Assert.Equal("/swagger/index.html", path);
    }

    [Theory]
    [InlineData("Development", "false")] // explicitly off
    [InlineData("Production", null)]     // off by default outside Development
    public async Task Swagger_is_off_when_disabled_or_in_production_by_default(string environment, string? setting)
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            if (setting is not null) b.UseSetting("SWAGGER_ENABLED", setting);
        }).CreateClient();
        foreach (var path in new[] { "/openapi/v1.json", "/swagger/index.html" })
        {
            var res = await client.GetAsync(path, TestContext.Current.CancellationToken);
            var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            // Not served at all: the SPA fallback skips file-like paths, so these are 404 (no OpenAPI JSON, no UI).
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.DoesNotContain("\"openapi\"", body);
            Assert.DoesNotContain("swagger-ui", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Swagger_and_openapi_are_404_not_the_spa_when_a_real_index_html_exists()
    {
        var webRoot = Directory.CreateTempSubdirectory("desk-spa-stub-");
        const string marker = "spa-stub-index-7f3c";
        try
        {
            File.WriteAllText(
                Path.Combine(webRoot.FullName, "index.html"),
                $"<!doctype html><html><body>{marker}</body></html>");

            var client = factory.WithWebHostBuilder(b =>
            {
                b.UseEnvironment("Production");
                b.UseWebRoot(webRoot.FullName);
            }).CreateClient();

            var home = await client.GetAsync("/", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, home.StatusCode);
            var homeBody = await home.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains(marker, homeBody);

            var spaRoute = await client.GetAsync("/positions", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, spaRoute.StatusCode);
            Assert.Contains(marker, await spaRoute.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

            foreach (var path in new[]
            {
                "/swagger", "/swagger/", "/swagger/index.html", "/swagger/other",
                "/openapi/v1.json", "/openapi/other",
            })
            {
                var res = await client.GetAsync(path, TestContext.Current.CancellationToken);
                var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
                Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
                Assert.DoesNotContain(marker, body);
                Assert.DoesNotContain("swagger-ui", body, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("\"openapi\"", body);
            }
        }
        finally
        {
            webRoot.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task Swagger_can_be_enabled_in_production()
    {
        var client = factory.WithWebHostBuilder(b => b.UseEnvironment("Production").UseSetting("SWAGGER_ENABLED", "true")).CreateClient();
        var res = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("application/json", res.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("Development", "1")]
    [InlineData("Development", "yes")]
    [InlineData("Production", "1")]
    [InlineData("Production", "yes")]
    public async Task Invalid_SWAGGER_ENABLED_fails_safe_to_off(string environment, string setting)
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("SWAGGER_ENABLED", setting);
        }).CreateClient();

        foreach (var path in new[] { "/swagger", "/openapi/v1.json" })
        {
            var res = await client.GetAsync(path, TestContext.Current.CancellationToken);
            var body = await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
            Assert.DoesNotContain("\"openapi\"", body);
            Assert.DoesNotContain("swagger-ui", body, StringComparison.OrdinalIgnoreCase);
        }

        var health = await client.GetFromJsonAsync<HealthResponse>("/health", TestContext.Current.CancellationToken);
        Assert.Equal("ok", health!.Status);
        Assert.False(string.IsNullOrWhiteSpace(health.Version));
    }
}
