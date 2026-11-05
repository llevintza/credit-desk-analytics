using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Desk.Mcp.Tests;

/// <summary>
/// The built <c>desk-mcp</c> as a real child process on stdin/stdout, against the test API on a loopback Kestrel port:
/// stdout carries only the protocol (the client would fail on anything else) and stderr carries no token or email.
/// </summary>
[Collection(DeskApiCollection.Name)]
public sealed class StdioProcessTests(DeskApiFixture api)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_desk_mcp_process_serves_tools_over_stdio()
    {
        var host = api.Host();
        host.UseKestrel(0);
        host.StartServer();
        var address = host.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses
            .Select(a => new Uri(a)).First(a => a.Scheme == "http");
        var origin = $"http://127.0.0.1:{address.Port}";

        using var home = new ConfigHome();
        var email = await DeskApiFixture.CreateUserAsync(host, DateTimeOffset.UtcNow);
        var login = new DeskMcpApp(TextWriter.Null, new FakePrompt(email, DeskApiFixture.Password), home.Env, TimeProvider.System);
        Assert.Equal(0, await login.RunAsync(["login", "--api", origin], Stream.Null, Stream.Null, Ct));
        var token = home.Store(origin).Read()!.RefreshToken;

        var stderr = new ConcurrentQueue<string>();
        var dll = Path.Combine(AppContext.BaseDirectory, "Desk.Mcp.dll");
        await using (var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "desk",
            Command = "dotnet",
            Arguments = [dll, "serve", "--api", origin],
            EnvironmentVariables = new Dictionary<string, string?> { ["XDG_CONFIG_HOME"] = home.Path, ["DATABASE_URL"] = null },
            StandardErrorLines = stderr.Enqueue,
        }), cancellationToken: Ct))
        {
            Assert.Equal("desk", client.ServerInfo?.Name);
            var result = await client.CallToolAsync("list_portfolios", new Dictionary<string, object?>(), cancellationToken: Ct);
            var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
            Assert.False(result.IsError is true, text);
            Assert.True(JsonDocument.Parse(text).RootElement.GetProperty("rows").GetArrayLength() > 0);
        }

        var output = string.Join('\n', stderr);
        Assert.Contains("serving " + origin, output);
        Assert.DoesNotContain(token, output);
        Assert.DoesNotContain(home.Store(origin).Read()!.RefreshToken, output);
        Assert.DoesNotContain(email, output);
    }
}
