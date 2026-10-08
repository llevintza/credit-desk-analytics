using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using IPNetwork = System.Net.IPNetwork;

namespace Desk.Api.Limits;

/// <summary>
/// What <see cref="ClientAddress"/> sees in production, so a shared login bucket can be diagnosed from the logs
/// instead of guessed at (#165, #116). Three lines, all structured:
/// <list type="bullet">
/// <item>at start-up, the resolver mode and the host's effective forwarded-headers options;</item>
/// <item>once per process and endpoint policy, the first rate-limited request;</item>
/// <item>at most every <see cref="FallbackInterval"/> per <see cref="ClientAddress.Source"/>, a request behind the proxy whose key every client shares.</item>
/// </list>
/// No raw client address is logged: the socket peer and private hops are infrastructure and are logged as they are,
/// a public peer is cut to its /24 or /48, and <c>CF-Connecting-IP</c> and the key are keyed hashes (a random key per
/// process, so two clients can be told apart in one process's logs and never reversed).
/// </summary>
public sealed class ClientAddressDiagnostics(
    ClientAddress clients,
    IOptions<ForwardedHeadersOptions> forwarded,
    TimeProvider time,
    ILogger<ClientAddressDiagnostics> logger) : IHostedService
{
    internal static readonly TimeSpan FallbackInterval = TimeSpan.FromMinutes(10);

    private static readonly IPNetwork SixToFour = IPNetwork.Parse("2002::/16");

    private readonly byte[] _hashKey = RandomNumberGenerator.GetBytes(32);
    private readonly ConcurrentDictionary<string, byte> _rejected = new();
    // One throttle per Source, so a steady fallback of one kind can't mask another (R175-05). Monotonic: elapsed
    // ticks since start, so a wall-clock step can't mute the warning.
    private readonly long _started = time.GetTimestamp();
    private readonly long[] _nextFallback = new long[Enum.GetValues<ClientAddress.Source>().Length];
    private readonly int[] _suppressed = new int[Enum.GetValues<ClientAddress.Source>().Length];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var o = forwarded.Value;
        logger.LogInformation(
            "Client address resolver: BehindProxy={BehindProxy} RenderNetworks={RenderNetworks} CloudflareRanges={CloudflareRanges} ForwardedHeaders={ForwardedHeaders} KnownNetworks={KnownNetworks} KnownProxies={KnownProxies} ForwardLimit={ForwardLimit}",
            clients.BehindProxy,
            string.Join(' ', ClientAddress.Render),
            ClientAddress.Cloudflare.Length,
            o.ForwardedHeaders,
            string.Join(' ', o.KnownIPNetworks),
            string.Join(' ', o.KnownProxies),
            o.ForwardLimit?.ToString(CultureInfo.InvariantCulture) ?? "none");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// The first rejected request on each endpoint policy (<c>login</c>, or <c>global</c> for endpoints without one),
    /// once per process, so a 429 elsewhere can't use up the login line (R175-08). The policy is the endpoint's, not
    /// necessarily the limiter that rejected it. Bounded: one entry per policy name.
    /// </summary>
    public void Rejected(HttpContext http)
    {
        var policy = http.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName ?? "global";
        if (!_rejected.TryAdd(policy, 0))
            return;
        Log(LogLevel.Warning, "First rate-limited request", http, policy, 0);
    }

    /// <summary>A request behind the proxy keyed on something every client shares (throttled).</summary>
    public void Fallback(HttpContext http, ClientAddress.Source source)
    {
        // Each limiter resolves the key again; count the request once, not each lookup (R175-06).
        if (!http.Items.TryAdd(typeof(ClientAddressDiagnostics), null))
            return;
        var i = (int)source;
        var now = time.GetElapsedTime(_started).Ticks;
        var next = Interlocked.Read(ref _nextFallback[i]);
        if (now < next || Interlocked.CompareExchange(ref _nextFallback[i], now + FallbackInterval.Ticks, next) != next)
        {
            Interlocked.Increment(ref _suppressed[i]);
            return;
        }
        Log(LogLevel.Warning, $"Client address fell back to a shared key ({source})", http, null, Interlocked.Exchange(ref _suppressed[i], 0));
    }

    private void Log(LogLevel level, string what, HttpContext http, string? policy, int suppressed)
    {
        try
        {
            Write(level, what, http, policy, suppressed);
        }
        catch (Exception)
        {
            // Diagnostics must never change the response: a failing logger can't turn a 429 into a 500 (R175-03).
        }
    }

    private void Write(LogLevel level, string what, HttpContext http, string? policy, int suppressed)
    {
        var peer = http.Connection.RemoteIpAddress;
        var headers = http.Request.Headers;
        var address = ClientAddress.Resolve(peer, headers, clients.BehindProxy, out var source);
        var chain = ClientAddress.ForwardedFor(headers);
        logger.Log(
            level,
            "{What}: Route={Route} EndpointPolicy={EndpointPolicy} Source={Source} Peer={Peer} PeerIsRender={PeerIsRender} ForwardedForLines={ForwardedForLines} ForwardedForHops={ForwardedForHops} ForwardedForShape={ForwardedForShape} CfConnectingIp={CfConnectingIp} TrueClientIp={TrueClientIp} Key={Key} Suppressed={Suppressed}",
            what,
            // The route template, never the raw path: that is the caller's text, of any length (R175-04).
            (http.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "(unmatched)",
            policy,
            source,
            Redact(peer),
            peer is not null && ClientAddress.In(ClientAddress.Render, peer),
            headers["X-Forwarded-For"].Count,
            chain.Length,
            string.Join('>', chain.Select(Classify)),
            Hashed(headers["CF-Connecting-IP"].ToString()),
            headers.ContainsKey("True-Client-IP") ? "present" : "absent",
            address is null ? "unknown" : Hash(address.ToString()),
            suppressed);
    }

    /// <summary>The kind of each <c>X-Forwarded-For</c> entry, left to right, without the address.</summary>
    internal static string Classify(string entry) =>
        !IPAddress.TryParse(entry, out var ip) ? "invalid"
        : ClientAddress.In(ClientAddress.Cloudflare, ip) ? "cf"
        : ClientAddress.In(ClientAddress.Render, ip) ? "render"
        : ClientAddress.In(ClientAddress.Private, ip) ? "private"
        : "public";

    /// <summary>A private peer is infrastructure and logged whole; a public one is cut to its /24 or /48 (/40 for 6to4).</summary>
    internal static string Redact(IPAddress? ip)
    {
        if (ip is null)
            return "none";
        var address = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        if (ClientAddress.In(ClientAddress.Private, address))
            return address.ToString();
        // 6to4 (2002::/16) embeds the IPv4 address in bits 16-48: cut to /40, the /24 of that address (R175-07).
        var prefix = address.AddressFamily == AddressFamily.InterNetwork ? 24 : SixToFour.Contains(address) ? 40 : 48;
        return $"{Network(address, prefix)} (public)";
    }

    private static IPNetwork Network(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++)
            bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPNetwork(new IPAddress(bytes), prefix);
    }

    private string Hashed(string header) =>
        header.Length == 0 ? "absent"
        : IPAddress.TryParse(header, out var ip) ? $"{(ip.AddressFamily == AddressFamily.InterNetwork ? "v4" : "v6")}:{Hash(ip.ToString())}"
        : "invalid";

    private string Hash(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_hashKey, Encoding.UTF8.GetBytes(value)))[..12];
}
