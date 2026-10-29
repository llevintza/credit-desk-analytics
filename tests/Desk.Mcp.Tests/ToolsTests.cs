using System.Security.Claims;
using Desk.Api.Positions;
using Desk.Data.Grid;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Desk.Mcp.Tests;

/// <summary>The #554 meta tools end to end: an MCP client, the stdio server, the API and Postgres (ADR-0031 §4, ADR-0021).</summary>
[Collection(DeskApiCollection.Name)]
public sealed class ToolsTests(DeskApiFixture api)
{
    /// <summary>Grants exactly the portfolios it was built with.</summary>
    private sealed class StubEntitlements(params int[] ids) : IPortfolioEntitlements
    {
        public IReadOnlyCollection<int> For(ClaimsPrincipal user, MetaSnapshot meta) => ids;
    }

    private async Task<(McpSession Session, ConfigHome Home)> SignedInAsync(IPortfolioEntitlements? entitlements = null)
    {
        var host = api.Host(entitlements is null ? null : s => s.AddSingleton(entitlements));
        var home = new ConfigHome();
        var email = await DeskApiFixture.CreateUserAsync(host, DateTimeOffset.UtcNow);
        Assert.Equal(0, await McpSession.LoginAsync(host, home, email, TextWriter.Null));
        return (await McpSession.StartAsync(host, home), home);
    }

    [Fact]
    public async Task List_portfolios_under_a_single_portfolio_stub_returns_exactly_that_portfolio()
    {
        var (session, home) = await SignedInAsync(new StubEntitlements(1));
        using var _ = home;
        await using var __ = session;
        var result = await session.JsonAsync("list_portfolios");
        Assert.Equal(["portfolio_id", "name", "fund_id", "fund_name"], result.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        var row = Assert.Single(result.GetProperty("rows").EnumerateArray());
        Assert.Equal(1, row[0].GetInt32());
        Assert.False(string.IsNullOrEmpty(row[1].GetString()));
        Assert.Equal(1, result.GetProperty("rows_returned").GetInt32());
        Assert.False(result.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public async Task List_portfolios_under_an_empty_set_returns_none()
    {
        var (session, home) = await SignedInAsync(new StubEntitlements());
        using var _ = home;
        await using var __ = session;
        var result = await session.JsonAsync("list_portfolios");
        Assert.Empty(result.GetProperty("rows").EnumerateArray());
        Assert.Equal(0, result.GetProperty("rows_matched").GetInt32());
    }

    [Fact]
    public async Task List_portfolios_with_every_portfolio_entitled_returns_more_than_one()
    {
        var (session, home) = await SignedInAsync();
        using var _ = home;
        await using var __ = session;
        var rows = (await session.JsonAsync("list_portfolios")).GetProperty("rows").EnumerateArray().ToList();
        Assert.True(rows.Count > 1);
        Assert.Equal(rows.Count, rows.Select(r => r[0].GetInt32()).Distinct().Count());
    }

    [Fact]
    public async Task Get_as_of_dates_returns_the_loaded_dates_newest_first()
    {
        var (session, home) = await SignedInAsync();
        using var _ = home;
        await using var __ = session;
        var result = await session.JsonAsync("get_as_of_dates");
        Assert.Equal(DeskApiFixture.AsOf.ToString("yyyy-MM-dd"), result.GetProperty("latest").GetString());
        var dates = result.GetProperty("rows").EnumerateArray().Select(r => DateOnly.Parse(r[0].GetString()!)).ToList();
        Assert.Equal(DeskApiFixture.AsOf, dates[0]);
        Assert.Equal(dates.OrderDescending(), dates);
    }

    [Fact]
    public async Task List_columns_returns_name_group_type_and_aggregation()
    {
        var (session, home) = await SignedInAsync();
        using var _ = home;
        await using var __ = session;
        var result = await session.JsonAsync("list_columns");
        Assert.Equal(["name", "group", "type", "aggregation"], result.GetProperty("columns").EnumerateArray().Select(c => c.GetString()));
        var rows = result.GetProperty("rows").EnumerateArray().ToList();
        var mv = Assert.Single(rows, r => r[0].GetString() == "market_value");
        Assert.All(Enumerable.Range(0, 4), i => Assert.False(string.IsNullOrEmpty(mv[i].GetString())));
        Assert.False(result.GetProperty("truncated").GetBoolean());
        Assert.Equal(rows.Count, result.GetProperty("rows_matched").GetInt32());
    }

    [Fact]
    public async Task The_server_offers_exactly_the_three_read_only_tools()
    {
        var (session, home) = await SignedInAsync();
        using var _ = home;
        await using var __ = session;
        var tools = await session.Client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(["get_as_of_dates", "list_columns", "list_portfolios"], tools.Select(t => t.Name).Order());
        Assert.All(tools, t =>
        {
            var a = t.ProtocolTool.Annotations!;
            Assert.True(a.ReadOnlyHint);
            Assert.True(a.IdempotentHint);
            Assert.False(a.OpenWorldHint);
            Assert.False(a.DestructiveHint);
            Assert.False(string.IsNullOrEmpty(t.Description));
        });
    }

    [Fact]
    public async Task A_tool_without_a_sign_in_asks_for_desk_mcp_login()
    {
        using var home = new ConfigHome();
        await using var session = await McpSession.StartAsync(api.Host(), home);
        var error = await session.ErrorAsync("list_portfolios");
        Assert.Contains("desk-mcp login --api http://localhost", error);
        Assert.Contains("Not signed in", session.Stderr.ToString());
    }
}
