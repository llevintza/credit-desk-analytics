using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Desk.Mcp;

/// <summary>
/// <c>desk-mcp login | logout | serve --api &lt;origin&gt; [--live]</c> (ADR-0031). Exit codes: 0 ok, 1 sign-in refused
/// or failed, 2 bad arguments or a refused origin, 130 cancelled. Everything but the protocol goes to <paramref name="stderr"/>.
/// </summary>
internal sealed class DeskMcpApp(TextWriter stderr, ICredentialPrompt prompt, Func<string, string?> env, TimeProvider time,
    Func<HttpMessageHandler>? handler = null)
{
    public const string Usage = """
        Usage:
          desk-mcp login  --api <origin> [--live]   sign in (prompts for email and password) and save the refresh token
          desk-mcp logout --api <origin> [--live]   end the saved sign-in and delete its token file
          desk-mcp serve  --api <origin> [--live]   run the MCP server on stdin/stdout
        http:// is allowed only for localhost; any host but a local stack also needs --live (ADR-0031 D17).
        """;

    public static string Version { get; } =
        typeof(DeskMcpApp).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public async Task<int> RunAsync(string[] args, Stream stdin, Stream stdout, CancellationToken ct)
    {
        if (!TryParse(args, out var command, out var api, out var live))
        {
            await stderr.WriteLineAsync(Usage);
            return 2;
        }
        if (ApiOrigin.TryParse(api, live, out var error) is not { } origin)
        {
            await stderr.WriteLineAsync(error);
            return 2;
        }
        if (TokenStore.ConfigDirectory(env) is not { } dir)
        {
            await stderr.WriteLineAsync("No user config directory: set HOME or XDG_CONFIG_HOME.");
            return 2;
        }
        var store = new TokenStore(dir, origin);
        try
        {
            return command switch
            {
                "login" => await LoginAsync(store, ct),
                "logout" => await LogoutAsync(store, ct),
                _ => await ServeAsync(store, stdin, stdout, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return 130;
        }
        catch (McpException e)
        {
            await stderr.WriteLineAsync(Redact.Text(e.Message));
            return 1;
        }
    }

    private static bool TryParse(string[] args, out string command, out string? api, out bool live)
    {
        command = args.Length > 0 ? args[0] : "";
        api = null;
        live = false;
        if (command is not ("login" or "logout" or "serve")) return false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--api" when i + 1 < args.Length && api is null:
                    api = args[++i];
                    break;
                case "--live":
                    live = true;
                    break;
                default:
                    return false;
            }
        }
        return api is not null;
    }

    private HttpClient NewHttpClient(ApiOrigin origin)
    {
        var http = new HttpClient(handler?.Invoke() ?? new SocketsHttpHandler { AllowAutoRedirect = false })
        {
            BaseAddress = origin.Uri,
            Timeout = TimeSpan.FromSeconds(30),
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"desk-mcp/{Version}");
        return http;
    }

    private DeskApiClient NewApiClient(TokenStore store, HttpClient http, ILoggerFactory logs) =>
        new(http, store, new RequestGate(time), time, logs.CreateLogger<DeskApiClient>());

    private async Task<int> LoginAsync(TokenStore store, CancellationToken ct)
    {
        if (!prompt.Interactive)
        {
            await stderr.WriteLineAsync("desk-mcp login needs a terminal: it prompts for the email and the password.");
            return 2;
        }
        var email = prompt.ReadLine("Email: ").Trim();
        var password = prompt.ReadSecret("Password: ");
        using var logs = LoggerFactory.Create(b => b.AddProvider(new StderrLoggerProvider(stderr)));
        using var http = NewHttpClient(store.Origin);
        var tokens = await NewApiClient(store, http, logs).LoginAsync(email, password, ct);
        if (tokens is null)
        {
            await stderr.WriteLineAsync("Login failed: invalid email or password, or the account is locked, disabled or expired.");
            return 1;
        }
        store.Write(tokens.RefreshToken);
        await stderr.WriteLineAsync($"Signed in to {store.Origin}. The refresh token is in {store.FilePath} (0600).");
        return 0;
    }

    private async Task<int> LogoutAsync(TokenStore store, CancellationToken ct)
    {
        using var logs = LoggerFactory.Create(b => b.AddProvider(new StderrLoggerProvider(stderr)));
        using var http = NewHttpClient(store.Origin);
        var ended = await NewApiClient(store, http, logs).LogoutAsync(ct);
        await stderr.WriteLineAsync(ended ? $"Signed out of {store.Origin}." : $"Not signed in to {store.Origin}.");
        return 0;
    }

    private async Task<int> ServeAsync(TokenStore store, Stream stdin, Stream stdout, CancellationToken ct)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(new StderrLoggerProvider(stderr));
        builder.Services.AddSingleton(time);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton(_ => NewHttpClient(store.Origin));
        builder.Services.AddSingleton(_ => new RequestGate(time));
        builder.Services.AddSingleton<DeskApiClient>();
        builder.Services
            .AddMcpServer(o => o.ServerInfo = new Implementation { Name = "desk", Title = "Credit desk (read-only)", Version = Version })
            .WithStreamServerTransport(stdin, stdout)
            .WithTools<DeskTools>();
        using var host = builder.Build();
        var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Desk.Mcp.Serve");
        if (store.Read() is null)
            log.LogWarning("Not signed in to {Origin}: tools will ask for `desk-mcp login --api {Origin}`", store.Origin.Value, store.Origin.Value);
        log.LogInformation("desk-mcp {Version} serving {Origin} on stdio", Version, store.Origin.Value);
        await host.RunAsync(ct);
        return 0;
    }
}
