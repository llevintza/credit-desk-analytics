using System.Net;
using Desk.Api.Limits;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using IPNetwork = System.Net.IPNetwork;

namespace Desk.Api.Tests;

/// <summary>#116: the client IP behind Cloudflare → Render, and which headers are believed (ADR-0005).</summary>
public sealed class ClientAddressTests
{
    private const string RenderLb = "10.214.3.7";     // Render's balancer: the socket peer in production
    private const string CfEdge = "162.158.90.14";    // a Cloudflare edge (162.158.0.0/15)
    private const string CfEdgeV6 = "2606:4700:10::ac43:1b0a";
    private const string Client = "203.0.113.7";
    private const string Spoof = "198.51.100.66";

    private static string? Resolve(string? peer, bool behindProxy = true, params (string Name, string Value)[] headers)
    {
        var h = new HeaderDictionary();
        foreach (var (name, value) in headers) h.Append(name, value);
        return ClientAddress.Resolve(peer is null ? null : IPAddress.Parse(peer), h, behindProxy)?.ToString();
    }

    [Fact]
    public void A_realistic_chain_resolves_to_cf_connecting_ip()
    {
        // The client sent its own XFF (spoof); Cloudflare appended the client, Render appended the edge.
        Assert.Equal(Client, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, {Client}, {CfEdge}"), ("CF-Connecting-IP", Client)));
    }

    [Fact]
    public void Two_clients_behind_one_edge_are_different_clients()
    {
        Assert.Equal("203.0.113.7", Resolve(RenderLb, true, ("X-Forwarded-For", $"203.0.113.7, {CfEdge}"), ("CF-Connecting-IP", "203.0.113.7")));
        Assert.Equal("203.0.113.8", Resolve(RenderLb, true, ("X-Forwarded-For", $"203.0.113.8, {CfEdge}"), ("CF-Connecting-IP", "203.0.113.8")));
    }

    [Fact]
    public void Without_cf_connecting_ip_the_key_is_the_edge_never_a_header_the_client_can_write()
    {
        // True-Client-IP and the XFF entries left of the edge pass through from the client unless Cloudflare writes
        // them: believing them would let a caller rotate its key or pin a victim's (R160-01).
        Assert.Equal(CfEdge, Resolve(RenderLb, true, ("X-Forwarded-For", $"198.51.100.8, {CfEdge}"), ("True-Client-IP", "198.51.100.7")));
        Assert.Equal(CfEdge, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, {Client}, {CfEdge}")));
        Assert.Equal(CfEdge, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, {Client}, {CfEdge}"), ("CF-Connecting-IP", "not-an-ip")));
        Assert.Equal(CfEdge, Resolve(RenderLb, true, ("X-Forwarded-For", CfEdge)));
        // Across several header lines, as some proxies send it: the rightmost entry is still the hop.
        Assert.Equal(Client, Resolve(RenderLb, true, ("X-Forwarded-For", Spoof), ("X-Forwarded-For", $"{Client}, {CfEdge}"), ("CF-Connecting-IP", Client)));
    }

    [Fact]
    public void Ipv6_edges_count_and_ipv4_mapped_peers_are_render()
    {
        Assert.Equal("2001:db8::7", Resolve(RenderLb, true, ("X-Forwarded-For", $"2001:db8::7, {CfEdgeV6}"), ("CF-Connecting-IP", "2001:db8::7")));
        // Kestrel on a dual-stack socket reports IPv4 peers as IPv4-mapped IPv6.
        Assert.Equal(Client, Resolve($"::ffff:{RenderLb}", true, ("X-Forwarded-For", $"{Client}, {CfEdge}"), ("CF-Connecting-IP", Client)));
    }

    [Fact]
    public void Headers_from_a_hop_that_is_not_cloudflare_are_ignored()
    {
        // Straight to Render: the hop Render saw is the client, whatever CF-* or XFF prefix it invented.
        Assert.Equal(Client, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, {Client}"), ("CF-Connecting-IP", Spoof), ("True-Client-IP", Spoof)));
    }

    [Fact]
    public void Headers_are_ignored_unless_the_peer_is_render()
    {
        // A peer outside Render's network wrote every header itself.
        Assert.Equal(Client, Resolve(Client, true, ("X-Forwarded-For", $"{Spoof}, {CfEdge}"), ("CF-Connecting-IP", Spoof)));
        // Off the proxy (local, compose, CI) the peer is always the client.
        Assert.Equal(RenderLb, Resolve(RenderLb, false, ("X-Forwarded-For", $"{Spoof}, {CfEdge}"), ("CF-Connecting-IP", Spoof)));
    }

    [Fact]
    public void A_missing_or_unparsable_forwarded_header_falls_back_to_the_peer()
    {
        Assert.Equal(RenderLb, Resolve(RenderLb, true));
        Assert.Equal(RenderLb, Resolve(RenderLb, true, ("X-Forwarded-For", " , ")));
        Assert.Equal(RenderLb, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Client}, not-an-ip")));
        Assert.Null(Resolve(null, true, ("CF-Connecting-IP", Client)));
        Assert.Equal("unknown", new ClientAddress(true).For(new DefaultHttpContext()));
    }

    [Theory]
    [InlineData(RenderLb, false, $"{Client}, {CfEdge}", Client, ClientAddress.Source.Peer, RenderLb)]
    [InlineData(Client, true, $"{Client}, {CfEdge}", Client, ClientAddress.Source.UntrustedPeer, Client)]
    [InlineData(RenderLb, true, null, Client, ClientAddress.Source.NoForwardedFor, RenderLb)]
    [InlineData(RenderLb, true, Client, null, ClientAddress.Source.DirectHop, Client)]
    [InlineData(RenderLb, true, "10.9.8.7, 172.20.0.4", Client, ClientAddress.Source.InternalHop, "172.20.0.4")]
    [InlineData(RenderLb, true, $"{Client}, {CfEdge}, 10.9.8.7", Client, ClientAddress.Source.CfConnectingIp, Client)]
    [InlineData(RenderLb, true, $"{Client}, garbage, 10.9.8.7", Client, ClientAddress.Source.NoForwardedFor, RenderLb)]
    [InlineData("172.20.0.4", true, $"{Client}, {CfEdge}", Client, ClientAddress.Source.CfConnectingIp, Client)]
    [InlineData(RenderLb, true, $"{Client}, {CfEdge}", Client, ClientAddress.Source.CfConnectingIp, Client)]
    [InlineData(RenderLb, true, $"{Client}, {CfEdge}", null, ClientAddress.Source.Edge, CfEdge)]
    public void Each_rule_reports_which_source_picked_the_key(string peer, bool behindProxy, string? xff, string? cf, ClientAddress.Source source, string expectedKey)
    {
        var h = new HeaderDictionary();
        if (xff is not null) h["X-Forwarded-For"] = xff;
        if (cf is not null) h["CF-Connecting-IP"] = cf;
        var key = ClientAddress.Resolve(IPAddress.Parse(peer), h, behindProxy, out var actual);
        Assert.Equal((source, expectedKey), (actual, key?.ToString()));
    }

    [Fact]
    public void Render_peers_and_trailing_internal_hops_anywhere_in_private_space_are_skipped()
    {
        // #165: the peer range and the hop layout behind Render aren't documented. A peer in any private range, and
        // any number of private hops after the Cloudflare edge, still resolve to CF-Connecting-IP.
        foreach (var peer in new[] { RenderLb, "172.20.0.4", "192.168.1.9", "100.64.1.1", "fd12:3456::1", $"::ffff:{RenderLb}" })
            Assert.Equal(Client, Resolve(peer, true, ("X-Forwarded-For", $"{Spoof}, {Client}, {CfEdge}, 10.1.2.3, 100.64.0.7"), ("CF-Connecting-IP", Client)));
    }

    [Fact]
    public void A_listed_trusted_hop_is_believed_as_a_peer_and_skipped_as_a_hop()
    {
        var trusted = ClientAddress.ParseNetworks("198.51.100.0/24, 2001:db8:ffff::/48");
        Assert.Equal(2, trusted.Length);
        var h = new HeaderDictionary { ["X-Forwarded-For"] = $"{Client}, {CfEdge}, 198.51.100.10", ["CF-Connecting-IP"] = Client };
        Assert.Equal(Client, ClientAddress.Resolve(IPAddress.Parse(RenderLb), h, true, trusted)?.ToString());
        Assert.Equal(Client, ClientAddress.Resolve(IPAddress.Parse("198.51.100.20"), h, true, trusted)?.ToString());
        // Unlisted, the same hop is a public non-Cloudflare hop: keyed on it (a direct client, or a shared hop).
        Assert.Equal("198.51.100.10", ClientAddress.Resolve(IPAddress.Parse(RenderLb), h, true)?.ToString());
    }

    [Theory]
    [InlineData("162.158.0.0/15")] // a Cloudflare range listed by mistake
    [InlineData("0.0.0.0/0")]      // or a range wide enough to cover Cloudflare (start-up rejects it; the resolver holds anyway)
    public void A_trusted_list_never_skips_a_cloudflare_hop(string listed)
    {
        // A Worker reaching Render directly arrives from a Cloudflare address with a forged prefix. Skipping that hop
        // as trusted would key on the forged entry; it must resolve exactly as with no list (the edge, R160-01).
        IPNetwork[] trusted = [IPNetwork.Parse(listed)];
        var h = new HeaderDictionary { ["X-Forwarded-For"] = $"{Spoof}, {CfEdge}" };
        Assert.Equal(CfEdge, ClientAddress.Resolve(IPAddress.Parse(RenderLb), h, true, out var source, trusted)?.ToString());
        Assert.Equal(ClientAddress.Source.Edge, source);
        Assert.Equal(CfEdge, ClientAddress.Resolve(IPAddress.Parse(RenderLb), h, true)?.ToString());
    }

    [Fact]
    public void A_client_cannot_hide_behind_hops_it_writes_itself()
    {
        // Only entries to the right of the hop Render vouches for are skipped. A forged edge and private hops written
        // by a client that reached Render directly sit left of the client's own address, which Render appended.
        Assert.Equal("192.0.2.50", Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, {CfEdge}, 10.0.0.1, 192.0.2.50"), ("CF-Connecting-IP", Spoof)));
        // Through Cloudflare, a forged private hop in the client's prefix is left of the edge: CF-Connecting-IP wins.
        Assert.Equal(Client, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, 10.0.0.1, {Client}, {CfEdge}"), ("CF-Connecting-IP", Client)));
    }

    [Theory]
    [InlineData("1.0.0.0/8")]                         // IPv4 at the cap
    [InlineData("10.0.0.0/8, 203.0.113.0/24")]        // several entries, comma
    [InlineData("198.51.100.0/24 2001:db8:ffff::/48")] // space separated, mixed families
    [InlineData("2001:db8::/32")]                     // IPv6 at the cap
    [InlineData("198.51.100.7/32, 2001:db8::1/128")]  // single hosts
    [InlineData("::fffe:0:0/96")]                     // next to the IPv4-mapped block, not in it
    public void Trusted_hops_at_or_narrower_than_the_cap_are_accepted(string value)
    {
        var parsed = ClientAddress.ParseNetworks(value);
        Assert.Equal(value.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Length, parsed.Length);
    }

    [Theory]
    [InlineData("0.0.0.0/7", "0.0.0.0/7")]                   // one bit wider than the IPv4 cap
    [InlineData("0.0.0.0/0", "0.0.0.0/0")]                   // every IPv4 address
    [InlineData("198.51.100.0/24, 2001:db8::/31", "2001:db8::/31")] // one bit wider than the IPv6 cap, after a good entry
    [InlineData("::/0", "::/0")]                             // every IPv6 address
    [InlineData("2001::/16", "2001::/16")]                   // a whole IPv6 block
    public void Start_up_rejects_a_trusted_hop_wider_than_the_cap(string value, string offending)
    {
        var e = Assert.Throws<InvalidOperationException>(() => ClientAddress.ParseNetworks(value));
        Assert.Contains($"'{offending}'", e.Message);
        Assert.Contains(ClientAddress.TrustedHopsKey, e.Message);
        Assert.Contains(offending.Contains(':') ? "/32" : "/8", e.Message);
    }

    [Theory]
    [InlineData("garbage")]
    [InlineData("198.51.100.0/33")]  // prefix past the address length
    [InlineData("198.51.100.1/24")]  // host bits set: probably a typo for a different range
    [InlineData("198.51.100.0/")]
    [InlineData("198.51.100.7")]     // a bare address: write 198.51.100.7/32
    [InlineData("2001:db8::/129")]
    public void Start_up_rejects_a_trusted_hop_that_is_not_a_cidr(string entry)
    {
        // Dropping a typo would quietly trust less than the operator meant, and the line that says so is easy to miss.
        var e = Assert.Throws<InvalidOperationException>(() => ClientAddress.ParseNetworks($"203.0.113.0/24, {entry}"));
        Assert.Contains($"'{entry}'", e.Message);
        Assert.DoesNotContain("203.0.113.0/24", e.Message); // names only the offending entry
    }

    [Theory]
    [InlineData("::ffff:0:0/96")]           // every IPv4 address, written as IPv6
    [InlineData("::ffff:198.51.100.0/120")] // one IPv4 /24, written as IPv6
    [InlineData("::/80")]                   // a range that holds the mapped block
    public void Start_up_rejects_an_ipv4_mapped_trusted_hop(string entry)
    {
        // The resolver compares IPv4-mapped peers and hops as IPv4, so such a range would never match: refuse it
        // rather than let the operator believe it is trusted.
        var e = Assert.Throws<InvalidOperationException>(() => ClientAddress.ParseNetworks(entry));
        Assert.Contains($"'{entry}'", e.Message);
        Assert.Contains("IPv4 form", e.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ,  ")]
    public void An_empty_trusted_hops_list_trusts_nothing_extra(string? value) => Assert.Empty(ClientAddress.ParseNetworks(value));

    [Fact]
    public void From_fails_on_an_over_wide_trusted_hop()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new(ClientAddress.TrustedHopsKey, "0.0.0.0/0")]).Build();
        Assert.Contains("'0.0.0.0/0'", Assert.Throws<InvalidOperationException>(() => ClientAddress.From(config)).Message);
    }

    [Fact]
    public void From_reads_the_trusted_hops()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new(ClientAddress.TrustedHopsKey, "198.51.100.0/24")]).Build();
        Assert.Equal("198.51.100.0/24", Assert.Single(ClientAddress.From(config).TrustedHops).ToString());
        Assert.Empty(ClientAddress.From(new ConfigurationBuilder().Build()).TrustedHops);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData("yes", false)]
    [InlineData(null, false)]
    public void Behind_the_proxy_follows_forwardedheaders_enabled(string? value, bool behind)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("FORWARDEDHEADERS_ENABLED", value)]).Build();
        var http = new DefaultHttpContext();
        http.Connection.RemoteIpAddress = IPAddress.Parse(RenderLb);
        http.Request.Headers["X-Forwarded-For"] = $"{Client}, {CfEdge}";
        http.Request.Headers["CF-Connecting-IP"] = Client;
        Assert.Equal(behind ? Client : RenderLb, ClientAddress.From(config).For(http));
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]            // IPv4-mapped: the same client as plain IPv4
    [InlineData("2001:db8:1:2::7", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2:ffff:ffff:ffff:ffff", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:3::7", "2001:db8:1:3::/64")]
    [InlineData("fe80::1%3", "fe80::/64")]
    [InlineData(null, "unknown")]
    public void Ipv6_clients_are_keyed_on_their_64_and_ipv4_mapped_on_the_ipv4_address(string? address, string key)
    {
        // #164: one end-user allocation is a /64. Keyed per /128, its holder would get 2^64 login windows.
        Assert.Equal(key, ClientAddress.Key(address is null ? null : IPAddress.Parse(address)));
    }

    [Fact]
    public void For_keys_cf_connecting_ip_and_the_peer_on_the_64()
    {
        var behind = new DefaultHttpContext();
        behind.Connection.RemoteIpAddress = IPAddress.Parse(RenderLb);
        behind.Request.Headers["X-Forwarded-For"] = $"2001:db8:5:6::9, {CfEdgeV6}";
        behind.Request.Headers["CF-Connecting-IP"] = "2001:db8:5:6::9";
        Assert.Equal("2001:db8:5:6::/64", new ClientAddress(true).For(behind));

        var direct = new DefaultHttpContext();
        direct.Connection.RemoteIpAddress = IPAddress.Parse($"::ffff:{Client}");
        Assert.Equal(Client, new ClientAddress(false).For(direct));
    }

    [Fact]
    public void The_forwarded_headers_middleware_keeps_proto_but_no_longer_rewrites_the_peer()
    {
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        new ClientAddress.ProtoOnly().PostConfigure(null, options);
        Assert.Equal(ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }
}
