using System.Text.Json;
using System.Text.Json.Serialization;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;

namespace Desk.Api.Auth;

/// <summary>
/// Optional demo accounts from the <c>DEMO_ACCOUNTS_JSON</c> secret (README §7.1). Absent means none.
/// An account is created on its first login attempt, never at startup: the app doesn't write to the
/// database while booting (AGENTS.md), and a login touches the database anyway.
/// </summary>
public sealed class DemoAccounts
{
    public const string ConfigKey = "DEMO_ACCOUNTS_JSON";

    private readonly Dictionary<string, DemoAccount> _byEmail;

    public DemoAccounts(IConfiguration config, ILogger<DemoAccounts> logger)
    {
        _byEmail = Parse(config[ConfigKey], logger);
    }

    /// <summary>Creates the demo account for <paramref name="email"/> if one is configured and it doesn't exist yet.</summary>
    public async Task EnsureAsync(string email, UserManager<DeskUser> users, CancellationToken ct)
    {
        if (!_byEmail.TryGetValue(email, out var demo) || await users.FindByEmailAsync(email) is not null)
            return;

        ct.ThrowIfCancellationRequested();
        var user = new DeskUser { UserName = demo.Email, Email = demo.Email, EmailConfirmed = true, ExpiresAt = demo.Expires };
        var created = await users.CreateAsync(user, demo.Password);
        // A concurrent first login may have created it already; either way the normal login path takes over.
        if (created.Succeeded)
            await users.AddToRoleAsync(user, demo.Role);
    }

    internal static Dictionary<string, DemoAccount> Parse(string? json, ILogger logger)
    {
        var result = new Dictionary<string, DemoAccount>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
            return result;

        DemoAccount[]? accounts;
        try
        {
            accounts = JsonSerializer.Deserialize(json, DemoAccountJson.Default.DemoAccountArray);
        }
        catch (JsonException)
        {
            // Never log the value: it holds passwords.
            logger.LogWarning("{Key} is not a valid JSON array of accounts; no demo accounts are available.", ConfigKey);
            return result;
        }

        foreach (var a in accounts ?? [])
        {
            if (string.IsNullOrWhiteSpace(a.Email) || string.IsNullOrEmpty(a.Password) || !Roles.IsKnown(a.Role))
            {
                logger.LogWarning("Skipping a demo account entry: it needs email, password and a role of viewer or admin.");
                continue;
            }
            result[a.Email] = a;
        }
        return result;
    }
}

public sealed record DemoAccount(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expires")] DateTimeOffset Expires);

[JsonSerializable(typeof(DemoAccount[]))]
internal sealed partial class DemoAccountJson : JsonSerializerContext;
