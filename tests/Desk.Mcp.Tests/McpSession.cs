using System.IO.Pipelines;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Desk.Mcp.Tests;

/// <summary>Answers the login prompt with fixed values, as a person at the terminal would.</summary>
internal sealed class FakePrompt(string email, string password, bool interactive = true) : ICredentialPrompt
{
    public bool Interactive => interactive;
    public string ReadLine(string prompt) => email;
    public string ReadSecret(string prompt) => password;
}

/// <summary>A user config directory of its own under the temp folder (never under the repository), deleted afterwards.</summary>
internal sealed class ConfigHome : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("desk-mcp-test-").FullName;

    public string? Env(string name) => name == "XDG_CONFIG_HOME" ? Path : null;

    public TokenStore Store(string origin = McpSession.Origin) =>
        new(TokenStore.ConfigDirectory(Env)!, ApiOrigin.TryParse(origin, live: false, out _)!);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}

/// <summary>
/// <c>desk-mcp serve</c> run in-process on a pair of pipes (its stdin and stdout), with an in-process MCP client on the
/// other ends, against a WebApplicationFactory host. Stderr is captured so tests can check what was logged.
/// </summary>
internal sealed class McpSession : IAsyncDisposable
{
    public const string Origin = "http://localhost";

    private readonly CancellationTokenSource _stop = new();
    private Task<int> _run = Task.FromResult(0);

    public McpClient Client { get; private set; } = null!;
    public StringWriter Stderr { get; } = new();
    public TextWriter SyncStderr { get; }

    private McpSession() => SyncStderr = TextWriter.Synchronized(Stderr);

    public static DeskMcpApp App(WebApplicationFactory<global::Program> host, ConfigHome home, TextWriter stderr, ICredentialPrompt? prompt = null,
        TimeProvider? time = null) =>
        new(stderr, prompt ?? new FakePrompt("", ""), home.Env, time ?? TimeProvider.System, () => host.Server.CreateHandler());

    /// <summary>Runs <c>desk-mcp login</c> against <paramref name="host"/>; returns the exit code.</summary>
    public static Task<int> LoginAsync(WebApplicationFactory<global::Program> host, ConfigHome home, string email, TextWriter stderr,
        string? password = null) =>
        App(host, home, stderr, new FakePrompt(email, password ?? DeskApiFixture.Password))
            .RunAsync(["login", "--api", Origin], Stream.Null, Stream.Null, TestContext.Current.CancellationToken);

    public static async Task<McpSession> StartAsync(WebApplicationFactory<global::Program> host, ConfigHome home, TimeProvider? time = null)
    {
        var session = new McpSession();
        var toServer = new Pipe();
        var toClient = new Pipe();
        var app = App(host, home, session.SyncStderr, time: time);
        session._run = Task.Run(() => app.RunAsync(["serve", "--api", Origin], toServer.Reader.AsStream(), toClient.Writer.AsStream(), session._stop.Token));
        session.Client = await McpClient.CreateAsync(
            new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()),
            cancellationToken: TestContext.Current.CancellationToken);
        return session;
    }

    public async Task<CallToolResult> CallAsync(string tool) =>
        await Client.CallToolAsync(tool, new Dictionary<string, object?>(), cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>Calls <paramref name="tool"/>, asserts it succeeded, and returns its JSON result.</summary>
    public async Task<JsonElement> JsonAsync(string tool)
    {
        var result = await CallAsync(tool);
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.False(result.IsError is true, text);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    /// <summary>Calls <paramref name="tool"/>, asserts it failed, and returns the error text.</summary>
    public async Task<string> ErrorAsync(string tool)
    {
        var result = await CallAsync(tool);
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }

    public async ValueTask DisposeAsync()
    {
        await Client.DisposeAsync();
        await _stop.CancelAsync();
        Assert.Contains(await _run, new[] { 0, 130 });
        _stop.Dispose();
    }
}
