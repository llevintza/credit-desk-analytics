using System.Net;
using Desk.Api.Limits;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Desk.Api.Tests;

/// <summary>#165: what the resolver logs in production, and that no raw client address is in it.</summary>
public sealed class ClientAddressDiagnosticsTests
{
    private const string RenderLb = "10.214.3.7";
    private const string CfEdge = "162.158.90.14";
    private const string Client = "203.0.113.7";
    private const string Spoof = "198.51.100.66";

    private readonly CapturingLogger _log = new();
    private readonly FakeTimeProvider _time = new();

    private ClientAddressDiagnostics Diagnostics(bool behindProxy = true, ForwardedHeadersOptions? forwarded = null) =>
        new(new ClientAddress(behindProxy), Options.Create(forwarded ?? new ForwardedHeadersOptions()), _time, _log);

    private static DefaultHttpContext Request(ClientAddressDiagnostics? diagnostics, string peer, string? xff = null, string? cf = null, string path = "/api/auth/login")
    {
        var http = new DefaultHttpContext();
        if (diagnostics is not null)
            http.RequestServices = new ServiceCollection().AddSingleton(diagnostics).BuildServiceProvider();
        http.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        http.Request.Path = path;
        if (xff is not null) http.Request.Headers["X-Forwarded-For"] = xff;
        if (cf is not null) http.Request.Headers["CF-Connecting-IP"] = cf;
        return http;
    }

    [Fact]
    public async Task Start_up_logs_the_resolver_mode_and_the_effective_forwarded_headers_options()
    {
        var forwarded = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedProto, ForwardLimit = 1 };
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
        forwarded.KnownProxies.Add(IPAddress.Parse("10.1.2.3"));
        await Diagnostics(forwarded: forwarded).StartAsync(TestContext.Current.CancellationToken);

        var line = Assert.Single(_log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("BehindProxy=True", line.Text);
        Assert.Contains("RenderNetworks=10.0.0.0/8", line.Text);
        Assert.Contains($"CloudflareRanges={ClientAddress.Cloudflare.Length}", line.Text);
        Assert.Contains("ForwardedHeaders=XForwardedProto", line.Text);
        Assert.Contains("KnownProxies=10.1.2.3", line.Text);
        Assert.Contains("ForwardLimit=1", line.Text);

        _log.Lines.Clear();
        var off = Diagnostics(behindProxy: false, new ForwardedHeadersOptions { ForwardLimit = null });
        await off.StartAsync(TestContext.Current.CancellationToken);
        await off.StopAsync(TestContext.Current.CancellationToken);
        Assert.Contains("BehindProxy=False", Assert.Single(_log.Lines).Text);
        Assert.Contains("ForwardLimit=none", _log.Lines.Single().Text);
    }

    [Fact]
    public void The_first_rejection_is_logged_once_with_the_chain_shape_and_hashed_addresses()
    {
        var diagnostics = Diagnostics();
        var http = Request(diagnostics, RenderLb, $"{Spoof}, {Client}, {CfEdge}", Client);
        http.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(new EnableRateLimitingAttribute(RateLimiting.LoginPolicy)), "login"));
        diagnostics.Rejected(http);
        diagnostics.Rejected(Request(diagnostics, RenderLb, $"{Client}, {CfEdge}", Client));

        var line = Assert.Single(_log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.StartsWith("First rate-limited request:", line.Text);
        Assert.Contains("Path=/api/auth/login", line.Text);
        Assert.Contains("Policy=login", line.Text);
        Assert.Contains("Source=CfConnectingIp", line.Text);
        Assert.Contains($"Peer={RenderLb}", line.Text);
        Assert.Contains("PeerIsRender=True", line.Text);
        Assert.Contains("ForwardedForLines=1", line.Text);
        Assert.Contains("ForwardedForHops=3", line.Text);
        Assert.Contains("ForwardedForShape=public>public>cf", line.Text);
        Assert.Matches("CfConnectingIp=v4:[0-9a-f]{12} ", line.Text);
        Assert.Contains("TrueClientIp=absent", line.Text);
        Assert.Matches("Key=[0-9a-f]{12} ", line.Text);
        Assert.DoesNotContain(Client, line.Text);
        Assert.DoesNotContain(Spoof, line.Text);
        Assert.DoesNotContain(CfEdge, line.Text);
    }

    [Fact]
    public void A_global_limiter_rejection_without_a_peer_still_logs()
    {
        var diagnostics = Diagnostics();
        var http = new DefaultHttpContext();
        http.Request.Headers["CF-Connecting-IP"] = "not-an-ip";
        http.Request.Headers["True-Client-IP"] = Spoof;
        diagnostics.Rejected(http);

        var text = Assert.Single(_log.Lines).Text;
        Assert.Contains("Policy=global", text);
        Assert.Contains("Source=UntrustedPeer", text);
        Assert.Contains("Peer=none", text);
        Assert.Contains("PeerIsRender=False", text);
        Assert.Contains("CfConnectingIp=invalid", text);
        Assert.Contains("TrueClientIp=present", text);
        Assert.Contains("Key=unknown", text);
    }

    [Fact]
    public void Two_clients_hash_apart_and_one_client_hashes_the_same()
    {
        var diagnostics = Diagnostics();
        string CfHash(string ip)
        {
            _log.Lines.Clear();
            _time.Advance(ClientAddressDiagnostics.FallbackInterval);
            // Logged through the fallback path: the edge is the key, CF-Connecting-IP is only hashed.
            diagnostics.Fallback(Request(null, RenderLb, $"{ip}, {CfEdge}", ip), ClientAddress.Source.Edge);
            return System.Text.RegularExpressions.Regex.Match(_log.Lines.Single().Text, "CfConnectingIp=(v[46]:[0-9a-f]{12})").Groups[1].Value;
        }
        Assert.Equal(CfHash("203.0.113.7"), CfHash("203.0.113.7"));
        Assert.NotEqual(CfHash("203.0.113.7"), CfHash("203.0.113.8"));
        Assert.StartsWith("v6:", CfHash("2001:db8::7"));
    }

    [Fact]
    public void Shared_key_fallbacks_warn_at_most_every_interval_with_the_suppressed_count()
    {
        var diagnostics = Diagnostics();
        var clients = new ClientAddress(true);
        // Peer outside Render's network (condition 2 of #165).
        Assert.Equal("100.64.3.7", clients.For(Request(diagnostics, "100.64.3.7", $"{Client}, {CfEdge}", Client)));
        // An internal hop appended after the edge (condition 3), and no X-Forwarded-For at all.
        clients.For(Request(diagnostics, RenderLb, $"{Client}, {CfEdge}, 10.9.8.7", Client));
        clients.For(Request(diagnostics, RenderLb));

        var first = Assert.Single(_log.Lines);
        Assert.StartsWith("Client address fell back to a shared key (UntrustedPeer):", first.Text);
        Assert.Contains("Peer=100.64.3.7", first.Text);
        Assert.Contains("PeerIsRender=False", first.Text);
        Assert.Contains("Suppressed=0", first.Text);

        _time.Advance(ClientAddressDiagnostics.FallbackInterval);
        clients.For(Request(diagnostics, RenderLb, $"{Client}, {CfEdge}, 10.9.8.7", Client));
        Assert.Equal(2, _log.Lines.Count);
        var second = _log.Lines.Last().Text;
        Assert.StartsWith("Client address fell back to a shared key (InternalHop):", second);
        Assert.Contains("ForwardedForShape=public>cf>render", second);
        Assert.Contains("Suppressed=2", second);
        Assert.DoesNotContain(Client, string.Join('\n', _log.Lines.Select(l => l.Text)));
    }

    [Fact]
    public void A_public_hop_outside_the_lists_warns_only_when_cloudflare_vouched_for_a_client()
    {
        var diagnostics = Diagnostics();
        var clients = new ClientAddress(true);
        // An edge in a Cloudflare range the list doesn't have yet: the key is that hop for every client.
        Assert.Equal("8.8.8.8", clients.For(Request(diagnostics, RenderLb, $"{Client}, 8.8.8.8", Client)));
        var line = Assert.Single(_log.Lines).Text;
        Assert.StartsWith("Client address fell back to a shared key (DirectHop):", line);
        Assert.Contains("ForwardedForShape=public>public", line);
        Assert.Matches("CfConnectingIp=v4:[0-9a-f]{12} ", line);

        // A client that reached Render directly has no CF header: it is its own key, nothing to warn about.
        _log.Lines.Clear();
        _time.Advance(ClientAddressDiagnostics.FallbackInterval);
        Assert.Equal("8.8.8.8", clients.For(Request(diagnostics, RenderLb, $"{Client}, 8.8.8.8")));
        Assert.Empty(_log.Lines);
    }

    [Fact]
    public void Per_client_keys_and_the_unproxied_host_never_warn()
    {
        var diagnostics = Diagnostics();
        new ClientAddress(true).For(Request(diagnostics, RenderLb, $"{Client}, {CfEdge}", Client));
        new ClientAddress(true).For(Request(diagnostics, RenderLb, Client));
        new ClientAddress(false).For(Request(diagnostics, RenderLb));
        Assert.Empty(_log.Lines);
        // A host without the diagnostics (or a bare context) still resolves the shared key.
        var bare = Request(null, RenderLb);
        bare.RequestServices = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(RenderLb, new ClientAddress(true).For(bare));
    }

    [Theory]
    [InlineData(CfEdge, "cf")]
    [InlineData("2606:4700:10::ac43:1b0a", "cf")]
    [InlineData(RenderLb, "render")]
    [InlineData("172.20.0.4", "private")]
    [InlineData("100.64.0.9", "private")]
    [InlineData("fd00::1", "private")]
    [InlineData(Client, "public")]
    [InlineData("unknown", "invalid")]
    public void Each_forwarded_for_entry_is_logged_as_its_kind(string entry, string kind) =>
        Assert.Equal(kind, ClientAddressDiagnostics.Classify(entry));

    [Theory]
    [InlineData(null, "none")]
    [InlineData(RenderLb, RenderLb)]
    [InlineData("::ffff:10.214.3.7", RenderLb)]
    [InlineData("100.70.1.2", "100.70.1.2")]
    [InlineData("203.0.113.77", "203.0.113.0/24 (public)")]
    [InlineData("2001:db8:1234:5678::9", "2001:db8:1234::/48 (public)")]
    public void A_private_peer_is_logged_whole_and_a_public_one_cut_to_its_network(string? peer, string logged) =>
        Assert.Equal(logged, ClientAddressDiagnostics.Redact(peer is null ? null : IPAddress.Parse(peer)));

    internal sealed class CapturingLogger : ILogger<ClientAddressDiagnostics>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Lines) Lines.Add((logLevel, formatter(state, exception)));
        }
    }
}
