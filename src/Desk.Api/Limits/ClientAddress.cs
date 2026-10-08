using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;
using Microsoft.Extensions.Options;

namespace Desk.Api.Limits;

/// <summary>
/// The client's IP for the per-IP login window and the anonymous token bucket (README §7.2, ADR-0005, #116).
/// <para>
/// In production a request travels client → Cloudflare edge → Render's load balancer → app. The socket peer is
/// Render's balancer (10.0.0.0/8), which appends the address that connected to it (a Cloudflare edge) to
/// <c>X-Forwarded-For</c>. Keying on either of those puts every client behind one edge in the same bucket, so one
/// client could lock everyone out of login. The real client is what Cloudflare reports in <c>CF-Connecting-IP</c>,
/// which Cloudflare always writes and overwrites. <c>True-Client-IP</c> and the rest of <c>X-Forwarded-For</c> are
/// not used: a client can write either and have it passed through. Without <c>CF-Connecting-IP</c> the key is the
/// edge (the old shared window, failing closed rather than letting the caller pick its key).
/// </para>
/// <para>
/// Every header is believed only when it was written by a hop we trust: the peer must be Render's network, and the
/// Cloudflare headers count only when the hop Render saw is a published Cloudflare range. Anything a client writes
/// itself (a spoofed <c>X-Forwarded-For</c> prefix, <c>CF-Connecting-IP</c> sent straight to Render) is ignored.
/// Off the proxy (<c>FORWARDEDHEADERS_ENABLED</c> unset: local, compose, CI) the socket peer is the client.
/// </para>
/// </summary>
public sealed class ClientAddress(bool behindProxy)
{
    /// <summary>Render's private network: the load balancer that connects to the app.</summary>
    internal static readonly IPNetwork[] Render = [IPNetwork.Parse("10.0.0.0/8")];

    /// <summary>Addresses no client on the internet connects from: RFC 1918, CGNAT, loopback, link-local, ULA.</summary>
    internal static readonly IPNetwork[] Private =
    [
        .. new[]
        {
            "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "100.64.0.0/10", "127.0.0.0/8", "169.254.0.0/16",
            "::1/128", "fc00::/7", "fe80::/10",
        }.Select(IPNetwork.Parse),
    ];

    /// <summary>
    /// https://www.cloudflare.com/ips-v4 and /ips-v6 (checked 2026-10-08; kept current by the scheduled check in #161).
    /// </summary>
    internal static readonly IPNetwork[] Cloudflare =
    [
        .. new[]
        {
            "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
            "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
            "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
            "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
            "2a06:98c0::/29", "2c0f:f248::/32",
        }.Select(IPNetwork.Parse),
    ];

    public static ClientAddress From(IConfiguration config) =>
        new(bool.TryParse(config["FORWARDEDHEADERS_ENABLED"], out var on) && on); // = ASPNETCORE_FORWARDEDHEADERS_ENABLED

    /// <summary>Whether the app runs behind Render's proxy (<c>FORWARDEDHEADERS_ENABLED</c>).</summary>
    public bool BehindProxy => behindProxy;

    public string For(HttpContext http)
    {
        var address = Resolve(http.Connection.RemoteIpAddress, http.Request.Headers, behindProxy, out var source);
        if (behindProxy && source is not (Source.CfConnectingIp or Source.DirectHop))
            http.RequestServices?.GetService<ClientAddressDiagnostics>()?.Fallback(http, source);
        return address?.ToString() ?? "unknown";
    }

    /// <summary>Which rule picked the key. Everything but <see cref="CfConnectingIp"/> and <see cref="DirectHop"/>
    /// shares one key across clients behind the proxy (#165).</summary>
    public enum Source
    {
        /// <summary>Off the proxy: the socket peer is the client.</summary>
        Peer,
        /// <summary>Behind the proxy, but the peer isn't in <see cref="Render"/>, so no header is believed.</summary>
        UntrustedPeer,
        /// <summary>The peer is Render but <c>X-Forwarded-For</c> is missing or its last entry isn't an address.</summary>
        NoForwardedFor,
        /// <summary>The hop Render saw isn't Cloudflare: it reached Render directly and is the client.</summary>
        DirectHop,
        /// <summary>The hop Render saw is a private address (an internal hop, not a client): every client shares it.</summary>
        InternalHop,
        /// <summary>Through Cloudflare, keyed on <c>CF-Connecting-IP</c>.</summary>
        CfConnectingIp,
        /// <summary>Through Cloudflare without a usable <c>CF-Connecting-IP</c>: keyed on the edge.</summary>
        Edge,
    }

    internal static IPAddress? Resolve(IPAddress? peer, IHeaderDictionary headers, bool behindProxy) =>
        Resolve(peer, headers, behindProxy, out _);

    internal static IPAddress? Resolve(IPAddress? peer, IHeaderDictionary headers, bool behindProxy, out Source source)
    {
        if (!behindProxy || peer is null || !In(Render, peer))
        {
            source = behindProxy ? Source.UntrustedPeer : Source.Peer;
            return peer;
        }

        // Render appends the address that connected to it, so the rightmost entry is the only one Render vouches for.
        var chain = ForwardedFor(headers);
        if (chain.Length == 0 || !IPAddress.TryParse(chain[^1], out var hop))
        {
            source = Source.NoForwardedFor;
            return peer;
        }
        if (!In(Cloudflare, hop))
        {
            source = In(Private, hop) ? Source.InternalHop : Source.DirectHop;
            return hop; // reached Render directly: that hop is the client, and any CF-* header is its own invention
        }

        // Only Cloudflare's own header; without it, the edge (R160-01).
        if (IPAddress.TryParse(headers["CF-Connecting-IP"].ToString(), out var client))
        {
            source = Source.CfConnectingIp;
            return client;
        }
        source = Source.Edge;
        return hop;
    }

    internal static string[] ForwardedFor(IHeaderDictionary headers) =>
        headers["X-Forwarded-For"].ToString().Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    internal static bool In(IPNetwork[] networks, IPAddress ip)
    {
        var address = ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
        return networks.Any(n => n.Contains(address));
    }

    /// <summary>
    /// The host's forwarded-headers middleware (ASPNETCORE_FORWARDEDHEADERS_ENABLED) keeps handling
    /// <c>X-Forwarded-Proto</c>, so the app sees https behind Render. It no longer rewrites the remote address from
    /// <c>X-Forwarded-For</c>: one trusted hop is Cloudflare's edge, not the client, and <see cref="Resolve"/> needs
    /// the real socket peer to know which headers to believe.
    /// </summary>
    internal sealed class ProtoOnly : IPostConfigureOptions<ForwardedHeadersOptions>
    {
        public void PostConfigure(string? name, ForwardedHeadersOptions options) =>
            options.ForwardedHeaders &= ~ForwardedHeaders.XForwardedFor;
    }
}
