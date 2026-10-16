using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.HttpOverrides;
using IPNetwork = System.Net.IPNetwork;
using Microsoft.Extensions.Options;

namespace Desk.Api.Limits;

/// <summary>
/// The client's IP for the per-IP login window and the anonymous token bucket (README §7.2, ADR-0005, #116).
/// <para>
/// In production a request travels client → Cloudflare edge → Render's load balancer → app. The socket peer is
/// Render's balancer (a private address), which appends the address that connected to it (a Cloudflare edge) to
/// <c>X-Forwarded-For</c>. Render's own internal hops may append private addresses after it, and those are skipped
/// (#165); so are the hops an operator lists in <c>CLIENT_ADDRESS_TRUSTED_HOPS</c>. Keying on either of those puts every client behind one edge in the same bucket, so one
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
public sealed class ClientAddress(bool behindProxy, IPNetwork[]? trustedHops = null)
{
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
    /// Render's network: the load balancer that connects to the app and its internal hops. Render doesn't document
    /// the range, and a peer outside 10.0.0.0/8 put every client in one bucket (#165), so any private address counts:
    /// no client on the internet can connect from one.
    /// </summary>
    internal static readonly IPNetwork[] Render = Private;

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

    public const string TrustedHopsKey = "CLIENT_ADDRESS_TRUSTED_HOPS";

    public static ClientAddress From(IConfiguration config) =>
        new(bool.TryParse(config["FORWARDEDHEADERS_ENABLED"], out var on) && on, // = ASPNETCORE_FORWARDEDHEADERS_ENABLED
            ParseNetworks(config[TrustedHopsKey]));

    /// <summary>Whether the app runs behind Render's proxy (<c>FORWARDEDHEADERS_ENABLED</c>).</summary>
    public bool BehindProxy => behindProxy;

    /// <summary>
    /// Public proxy ranges between Cloudflare and the app that the lists here don't know, from
    /// <c>CLIENT_ADDRESS_TRUSTED_HOPS</c> (CIDRs, comma or space separated). A peer in them is believed, and a trailing
    /// hop in them is skipped like an internal one, so a hop the diagnostics show as shared can be trusted from the
    /// dashboard without a release (#165). Never list a range clients connect from.
    /// </summary>
    public IPNetwork[] TrustedHops { get; } = trustedHops ?? [];

    /// <summary>
    /// The widest trusted hop allowed (#418). An IPv4 /8 is the widest private block (10.0.0.0/8, already trusted) and
    /// far wider than any proxy range (Cloudflare's widest is a /13); an IPv6 /32 is a whole RIR allocation to one
    /// network. Anything wider spans networks that clients connect from, whose forged <c>X-Forwarded-For</c> prefixes
    /// would then pick their login key; <c>0.0.0.0/0</c> and <c>::/0</c> are the extreme case.
    /// </summary>
    public const int MinIPv4Prefix = 8, MinIPv6Prefix = 32;

    /// <summary>
    /// Parses <c>CLIENT_ADDRESS_TRUSTED_HOPS</c>, and stops start-up on an entry that isn't a CIDR (a typo would trust
    /// less than meant, silently) or is wider than <see cref="MinIPv4Prefix"/> / <see cref="MinIPv6Prefix"/>. The
    /// message names only the offending entry: the value isn't secret, and nothing else from configuration is shown.
    /// </summary>
    internal static IPNetwork[] ParseNetworks(string? value) =>
        [.. (value ?? "").Split([',', ' '], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseTrustedHop)];

    private static IPNetwork ParseTrustedHop(string entry)
    {
        // IPNetwork masks off bits set past the prefix; such an entry is a typo for some other range, so it's refused.
        if (!IPNetwork.TryParse(entry, out var network) || !IPAddress.Parse(entry[..entry.IndexOf('/')]).Equals(network.BaseAddress))
            throw new InvalidOperationException(
                $"{TrustedHopsKey}: '{entry}' is not a CIDR range (address/prefix, with no bits set past the prefix).");
        var min = network.BaseAddress.AddressFamily == AddressFamily.InterNetworkV6 ? MinIPv6Prefix : MinIPv4Prefix;
        if (network.PrefixLength < min)
            throw new InvalidOperationException(
                $"{TrustedHopsKey}: '{entry}' is wider than /{min}, the widest trusted hop allowed; list only the proxy's own range.");
        return network;
    }

    public string For(HttpContext http)
    {
        var address = Resolve(http.Connection.RemoteIpAddress, http.Request.Headers, behindProxy, out var source, TrustedHops);
        // A real direct client sends no CF-Connecting-IP (Cloudflare 403s a forged one), so a DirectHop that carries
        // one is a Cloudflare or Render hop missing from the lists, and every client shares it (R175-01).
        if (behindProxy && (source is not (Source.CfConnectingIp or Source.DirectHop)
                            || (source is Source.DirectHop && http.Request.Headers.ContainsKey("CF-Connecting-IP"))))
            http.RequestServices?.GetService<ClientAddressDiagnostics>()?.Fallback(http, source);
        return Key(address);
    }

    /// <summary>
    /// The limiter key for a client address (#164). IPv6 is keyed on its /64, the normal end-user allocation: per
    /// /128, one holder would get 2^64 login windows. IPv4-mapped IPv6 (<c>::ffff:a.b.c.d</c>) is the IPv4 client.
    /// </summary>
    internal static string Key(IPAddress? address)
    {
        if (address is null)
            return "unknown";
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (address.AddressFamily != AddressFamily.InterNetworkV6)
            return address.ToString();
        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>Which rule picked the key. Everything but <see cref="CfConnectingIp"/> and a genuine
    /// <see cref="DirectHop"/> shares one key across clients behind the proxy (#165).</summary>
    public enum Source
    {
        /// <summary>Off the proxy: the socket peer is the client.</summary>
        Peer,
        /// <summary>Behind the proxy, but the peer isn't in <see cref="Render"/>, so no header is believed.</summary>
        UntrustedPeer,
        /// <summary>The peer is Render but <c>X-Forwarded-For</c> is missing or its last entry isn't an address.</summary>
        NoForwardedFor,
        /// <summary>
        /// The hop Render saw is public but not in <see cref="Cloudflare"/>: either a client that reached Render
        /// directly (the hop is that client), or, when <c>CF-Connecting-IP</c> is present, a Cloudflare or Render
        /// range missing from the lists, which every client shares (warned, #161).
        /// </summary>
        DirectHop,
        /// <summary>Every hop in <c>X-Forwarded-For</c> is private or trusted (no client in it): every client shares it.</summary>
        InternalHop,
        /// <summary>Through Cloudflare, keyed on <c>CF-Connecting-IP</c>.</summary>
        CfConnectingIp,
        /// <summary>Through Cloudflare without a usable <c>CF-Connecting-IP</c>: keyed on the edge.</summary>
        Edge,
    }

    internal static IPAddress? Resolve(IPAddress? peer, IHeaderDictionary headers, bool behindProxy, IPNetwork[]? trusted = null) =>
        Resolve(peer, headers, behindProxy, out _, trusted);

    internal static IPAddress? Resolve(IPAddress? peer, IHeaderDictionary headers, bool behindProxy, out Source source, IPNetwork[]? trusted = null)
    {
        trusted ??= [];
        if (!behindProxy || peer is null || !(In(Render, peer) || In(trusted, peer)))
        {
            source = behindProxy ? Source.UntrustedPeer : Source.Peer;
            return peer;
        }

        // Render appends the address that connected to it, and its internal hops (private, or listed as trusted) may
        // append theirs after it. A client can only write to the left of all of them, so skipping trailing internal
        // hops never reaches a value the client chose. The first other entry is the hop Render vouches for.
        var chain = ForwardedFor(headers);
        IPAddress? hop = null;
        var at = chain.Length - 1;
        for (; at >= 0; at--)
        {
            if (!IPAddress.TryParse(chain[at], out hop))
                break;
            // A Cloudflare hop always ends the skip, even when a trusted entry covers it: a Worker can reach Render
            // directly from a Cloudflare address, so skipping it would let a forged prefix choose the key (R160-01).
            if (In(Cloudflare, hop) || (!In(Render, hop) && !In(trusted, hop)))
                break;
        }
        if (hop is null) // none, or an entry that isn't an address
        {
            source = Source.NoForwardedFor;
            return peer;
        }
        if (at < 0)
        {
            source = Source.InternalHop;
            return IPAddress.Parse(chain[^1]); // only internal hops, no client in the chain: shared
        }
        if (!In(Cloudflare, hop))
        {
            source = Source.DirectHop;
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
