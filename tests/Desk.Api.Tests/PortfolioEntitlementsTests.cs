using System.Security.Claims;
using Desk.Api.Positions;
using Desk.Data.Auth;
using Desk.Data.Grid;
using Microsoft.Extensions.Configuration;

namespace Desk.Api.Tests;

/// <summary>ADR-0021: the two providers and the <c>ENTITLEMENTS_MODE</c> switch, without a database.</summary>
public sealed class PortfolioEntitlementsTests
{
    private static readonly DateTimeOffset Never = DateTimeOffset.MaxValue;

    private static MetaSnapshot Meta(params int[] ids) =>
        new([], null, [], "v", [.. ids.Select(id => new PortfolioInfo(id, $"P{id}", 1, "F1"))], Never, Never);

    private static ClaimsPrincipal User(string? role, params string[] portfolios)
    {
        var claims = portfolios.Select(p => new Claim(DeskClaimsFactory.PortfolioClaim, p)).ToList();
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void All_mode_gives_every_portfolio_whatever_the_claims()
    {
        Assert.Equal([1, 2, 3], new AllPortfolios().For(User(Roles.Viewer), Meta(1, 2, 3)).Order());
    }

    [Fact]
    public void Grants_mode_gives_an_admin_every_portfolio_without_grants()
    {
        // E1: admins need no grants.
        Assert.Equal([1, 2, 3], new GrantedPortfolios().For(User(Roles.Admin), Meta(1, 2, 3)).Order());
    }

    [Fact]
    public void Grants_mode_gives_a_viewer_only_the_granted_portfolios()
    {
        Assert.Equal([2], new GrantedPortfolios().For(User(Roles.Viewer, "2"), Meta(1, 2, 3)));
        Assert.Equal([1, 3], new GrantedPortfolios().For(User(Roles.Viewer, "3", "1"), Meta(1, 2, 3)).Order());
    }

    [Fact]
    public void Grants_mode_fails_closed_for_a_user_with_no_grants()
    {
        // E2: zero grants means zero portfolios, never "all".
        Assert.Empty(new GrantedPortfolios().For(User(Roles.Viewer), Meta(1, 2, 3)));
        Assert.Empty(new GrantedPortfolios().For(User(null), Meta(1, 2, 3)));
    }

    [Fact]
    public void Grants_for_portfolios_lost_in_a_reseed_drop_out()
    {
        // E2 reseed drift: the claims are intersected with the snapshot's portfolios.
        Assert.Equal([2], new GrantedPortfolios().For(User(Roles.Viewer, "2", "99"), Meta(1, 2, 3)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("-1")]
    [InlineData(" 2")]
    [InlineData("2.0")]
    [InlineData("99999999999")]
    public void A_malformed_portfolio_claim_grants_nothing(string value)
    {
        Assert.Empty(new GrantedPortfolios().For(User(Roles.Viewer, value), Meta(1, 2, 3, -1)));
    }

    private static IConfiguration Config(string? mode) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { [EntitlementsMode.ConfigKey] = mode }).Build();

    [Theory]
    [InlineData(null, typeof(AllPortfolios))]
    [InlineData("", typeof(AllPortfolios))]
    [InlineData("all", typeof(AllPortfolios))]
    [InlineData(" ALL ", typeof(AllPortfolios))]
    [InlineData("grants", typeof(GrantedPortfolios))]
    [InlineData("Grants", typeof(GrantedPortfolios))]
    public void The_mode_picks_the_provider_and_defaults_to_all(string? mode, Type expected) =>
        Assert.IsType(expected, EntitlementsMode.Create(Config(mode)));

    [Theory]
    [InlineData("grant")]
    [InlineData("none")]
    [InlineData("all,grants")]
    public void An_unknown_mode_throws_naming_the_setting(string mode)
    {
        var e = Assert.Throws<InvalidOperationException>(() => EntitlementsMode.Create(Config(mode)));
        Assert.Contains(EntitlementsMode.ConfigKey, e.Message);
        Assert.Contains("ADR-0021", e.Message);
    }
}
