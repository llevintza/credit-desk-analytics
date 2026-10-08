using System.Net;
using Desk.Api.Limits;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;

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
    public void Without_cloudflare_headers_the_entry_left_of_the_edge_is_the_client()
    {
        Assert.Equal(Client, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, {Client}, {CfEdge}")));
        Assert.Equal(Client, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, {Client}, {CfEdge}"), ("CF-Connecting-IP", "not-an-ip")));
        // Across several header lines, as some proxies send it.
        Assert.Equal(Client, Resolve(RenderLb, true, ("X-Forwarded-For", Spoof), ("X-Forwarded-For", $"{Client}, {CfEdge}")));
        // Nothing left of the edge, or junk there: the edge is all we know.
        Assert.Equal(CfEdge, Resolve(RenderLb, true, ("X-Forwarded-For", CfEdge)));
        Assert.Equal(CfEdge, Resolve(RenderLb, true, ("X-Forwarded-For", $"junk, {CfEdge}")));
    }

    [Fact]
    public void True_client_ip_is_the_fallback_and_ipv6_edges_count()
    {
        Assert.Equal(Client, Resolve(RenderLb, true, ("X-Forwarded-For", $"{Spoof}, {CfEdge}"), ("True-Client-IP", Client)));
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
        Assert.Equal(behind ? Client : RenderLb, ClientAddress.From(config).For(http));
    }

    [Fact]
    public void The_forwarded_headers_middleware_keeps_proto_but_no_longer_rewrites_the_peer()
    {
        var options = new ForwardedHeadersOptions { ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto };
        new ClientAddress.ProtoOnly().PostConfigure(null, options);
        Assert.Equal(ForwardedHeaders.XForwardedProto, options.ForwardedHeaders);
    }
}
