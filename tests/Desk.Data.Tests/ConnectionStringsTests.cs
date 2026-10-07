using System.Security.Cryptography;
using Desk.Data;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Desk.Data.Tests;

public sealed class ConnectionStringsTests
{
    [Fact]
    public void Normalize_passthrough_for_keyword_format()
    {
        const string raw = "Host=localhost;Database=creditdesk;Username=desk";
        Assert.Equal(raw, ConnectionStrings.Normalize("  " + raw + "  "));
    }

    [Fact]
    public void Normalize_postgres_uri_defaults_port_5432()
    {
        var n = ConnectionStrings.Normalize("postgres://desk@db.example/creditdesk");
        var b = new NpgsqlConnectionStringBuilder(n);
        Assert.Equal("db.example", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("creditdesk", b.Database);
        Assert.Equal("desk", b.Username);
        Assert.True(string.IsNullOrEmpty(b.Password));
    }

    [Fact]
    public void Normalize_postgresql_uri_with_port_and_sslmode()
    {
        var n = ConnectionStrings.Normalize("postgresql://desk@db.example:6543/creditdesk?sslmode=require");
        var b = new NpgsqlConnectionStringBuilder(n);
        Assert.Equal(6543, b.Port);
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("Allow", SslMode.Allow)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("require", SslMode.Require)]
    [InlineData("verify-ca", SslMode.VerifyCA)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    [InlineData("VerifyFull", SslMode.VerifyFull)]
    public void Normalize_parses_libpq_sslmode_including_hyphenated(string sslmode, SslMode expected)
    {
        var n = ConnectionStrings.Normalize($"postgres://desk@localhost/db?sslmode={sslmode}");
        Assert.Equal(expected, new NpgsqlConnectionStringBuilder(n).SslMode);
    }

    [Fact]
    public void Normalize_unknown_sslmode_throws()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            ConnectionStrings.Normalize("postgres://desk@localhost/db?sslmode=nope"));
        Assert.Contains("sslmode", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_uri_without_userinfo_and_query_flag_without_value()
    {
        var n = ConnectionStrings.Normalize("postgres://localhost/creditdesk?sslmode=disable&bare");
        var b = new NpgsqlConnectionStringBuilder(n);
        Assert.Equal("localhost", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.True(string.IsNullOrEmpty(b.Username));
        Assert.Equal(SslMode.Disable, b.SslMode);
    }

    [Fact]
    public void Normalize_ignores_channel_binding_and_unknown_query()
    {
        var n = ConnectionStrings.Normalize(
            "postgres://desk@localhost/db?sslmode=disable&channel_binding=require&foo=bar");
        var b = new NpgsqlConnectionStringBuilder(n);
        Assert.Equal(SslMode.Disable, b.SslMode);
        Assert.Equal("localhost", b.Host);
    }

    [Fact]
    public void Normalize_unescapes_user_database_and_runtime_password()
    {
        var password = "p/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));
        var uri = $"postgres://d%40sk:{Uri.EscapeDataString(password)}@db.example:6543/credit%5Fdesk?sslmode=require";
        var b = new NpgsqlConnectionStringBuilder(ConnectionStrings.Normalize(uri));
        Assert.Equal("d@sk", b.Username);
        Assert.Equal(password, b.Password);
        Assert.Equal("credit_desk", b.Database);
        Assert.Equal(6543, b.Port);
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Resolve_uses_source_setting_before_DATABASE_URL()
    {
        var config = Config(
            ("ConnectionStrings:App", "Host=app-host;Database=app;Username=desk"),
            ("DATABASE_URL", "Host=fallback;Database=fallback;Username=desk"));
        var n = ConnectionStrings.Resolve(config, ConnectionStrings.App);
        Assert.Equal("app-host", new NpgsqlConnectionStringBuilder(n).Host);
    }

    [Fact]
    public void Resolve_falls_back_to_DATABASE_URL_when_source_blank()
    {
        var config = Config(
            ("ConnectionStrings:Core", "  "),
            ("DATABASE_URL", "postgres://desk@neon.example/creditdesk?sslmode=require"));
        var n = ConnectionStrings.Resolve(config, ConnectionStrings.Core);
        var b = new NpgsqlConnectionStringBuilder(n);
        Assert.Equal("neon.example", b.Host);
        Assert.Equal(SslMode.Require, b.SslMode);
    }

    [Fact]
    public void Resolve_throws_when_nothing_is_configured()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            ConnectionStrings.Resolve(Config(), ConnectionStrings.Market));
        Assert.Contains("Market", ex.Message, StringComparison.Ordinal);
        Assert.Contains("DATABASE_URL", ex.Message, StringComparison.Ordinal);
    }

    static IConfiguration Config(params (string Key, string Value)[] pairs) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(pairs.ToDictionary(p => p.Key, p => (string?)p.Value))
            .Build();
}
