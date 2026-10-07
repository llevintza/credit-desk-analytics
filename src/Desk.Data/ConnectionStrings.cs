using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Desk.Data;

/// <summary>
/// Resolves the connection string for a logical data source (README §5.1).
/// Each source has its own setting (ConnectionStrings:Core, ...); all of them fall back to DATABASE_URL.
/// Accepts both Npgsql keyword format and postgres:// URIs (what Neon's console copies).
/// </summary>
public static class ConnectionStrings
{
    public const string Core = "Core";
    public const string Market = "Market";
    public const string Surveillance = "Surveillance";
    public const string Pricing = "Pricing";
    public const string Reference = "Reference";
    public const string App = "App";

    public static string Resolve(IConfiguration config, string source)
    {
        var raw = config.GetConnectionString(source);
        if (string.IsNullOrWhiteSpace(raw)) raw = config["DATABASE_URL"];
        if (string.IsNullOrWhiteSpace(raw))
            throw new InvalidOperationException(
                $"No connection string for source '{source}'. Set ConnectionStrings__{source} or DATABASE_URL.");
        return Normalize(raw);
    }

    public static string Normalize(string raw)
    {
        raw = raw.Trim();
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
            return raw;

        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port <= 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
            Username = userInfo.Length > 0 ? Uri.UnescapeDataString(userInfo[0]) : "",
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : null,
        };
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            var value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            switch (kv[0].ToLowerInvariant())
            {
                case "sslmode": b.SslMode = ParseSslMode(value); break;
                case "channel_binding": break; // libpq-only; Npgsql negotiates SCRAM channel binding itself
                default: break;
            }
        }
        return b.ConnectionString;
    }

    internal static SslMode ParseSslMode(string value)
    {
        var token = value.Replace("-", "", StringComparison.Ordinal);
        if (!Enum.TryParse<SslMode>(token, ignoreCase: true, out var mode) || !Enum.IsDefined(mode))
            throw new ArgumentException($"Unknown sslmode '{value}'.");
        return mode;
    }
}
