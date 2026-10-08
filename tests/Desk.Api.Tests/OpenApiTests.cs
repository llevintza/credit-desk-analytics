using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Desk.Api.Tests;

public sealed class OpenApiTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    [Theory]
    [InlineData("Development", null)]   // on by default in Development
    [InlineData("Production", "true")]  // opted in elsewhere
    public async Task Enabled_swagger_and_openapi_still_need_a_session(string environment, string? setting)
    {
        var client = factory.WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            if (setting is not null) b.UseSetting("SWAGGER_ENABLED", setting);
        }).CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var path in new[] { "/openapi/v1.json", "/swagger", "/swagger/index.html" })
        {
            var res = await client.GetAsync(path, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
            Assert.DoesNotContain("swagger-ui", await res.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.OrdinalIgnoreCase);
        }
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
