using System.Text.Json;
using System.Text.Json.Serialization;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.Auth;

/// <summary>
/// Optional demo accounts from the <c>DEMO_ACCOUNTS_JSON</c> secret (README §7.1). Absent means none.
/// An account is created on its first login attempt, never at startup: the app doesn't write to the
/// database while booting (AGENTS.md), and a login touches the database anyway.
/// </summary>
public sealed class DemoAccounts
{
    public const string ConfigKey = "DEMO_ACCOUNTS_JSON";
    /// <summary>The <c>granted_by</c> of a demo account's portfolio grants.</summary>
    public const string GrantedBy = ConfigKey;

    private readonly Dictionary<string, DemoAccount> _byEmail;
    private readonly ILogger<DemoAccounts> _logger;
    private readonly TimeProvider _time;

    public DemoAccounts(IConfiguration config, ILogger<DemoAccounts> logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _byEmail = Parse(config[ConfigKey], logger);
    }

    /// <summary>
    /// The account for <paramref name="email"/>, creating it first when it's a configured demo account that doesn't
    /// exist yet. One lookup for the common case; null when there is no such account.
    /// The entry applies only when the account is first created: a later change to its password, role, expiry or
    /// portfolios in <c>DEMO_ACCOUNTS_JSON</c> doesn't touch the existing account (manage that one with the UserAdmin CLI).
    /// The entry's <c>portfolios</c> become its grants (ADR-0021 E2), saved together with the role, so an account that has
    /// its role also has its grants; an entry without <c>portfolios</c> gets none.
    /// </summary>
    /// <param name="email">The login email.</param>
    /// <param name="users">The request's user manager.</param>
    /// <param name="db">The request's context, the one <paramref name="users"/> saves through.</param>
    /// <param name="ct">Request cancellation.</param>
    public async Task<DeskUser?> FindOrCreateAsync(string email, UserManager<DeskUser> users, DbContext db, CancellationToken ct)
    {
        var existing = await users.FindByEmailAsync(email);
        if (existing is not null || !_byEmail.TryGetValue(email, out var demo))
            return existing;

        ct.ThrowIfCancellationRequested();
        var user = new DeskUser { UserName = demo.Email, Email = demo.Email, EmailConfirmed = true, ExpiresAt = demo.Expires };
        IdentityResult created;
        try
        {
            created = await users.CreateAsync(user, demo.Password);
        }
        catch (DbUpdateException)
        {
            // Lost the race at the unique index: treated like Identity's own duplicate check below. The failed insert
            // leaves this entity tracked as Added, and the request's next save (a failed-attempt count, a lockout)
            // would retry the insert and fail again: stop tracking it.
            db.Entry(user).State = EntityState.Detached;
            created = IdentityResult.Failed(new IdentityError { Code = "DuplicateUserName" });
        }
        if (created.Succeeded)
        {
            // Tracked on the request's context, so the role's save writes them in the same transaction (ADR-0021 E2).
            var now = _time.GetUtcNow();
            var grants = (demo.Portfolios ?? []).Select(id => new PortfolioGrant { UserId = user.Id, PortfolioId = id, GrantedAt = now, GrantedBy = GrantedBy }).ToList();
            db.Set<PortfolioGrant>().AddRange(grants);
            var role = await FailureIfThrown(() => users.AddToRoleAsync(user, demo.Role));
            if (role.Succeeded)
                return user;

            // Not saved: stop tracking them, so the removal below (or the request's next save) doesn't try to insert them.
            foreach (var grant in grants)
                db.Entry(grant).State = EntityState.Detached;

            // An account without its role must not sign in: remove it, so the next login tries again. If the removal
            // fails too, LoginAsync still refuses the account (#275).
            var deleted = await FailureIfThrown(() => users.DeleteAsync(user));
            if (deleted.Succeeded)
                _logger.LogWarning("Demo account {Email} could not be given its role: {Errors}. It was removed.", demo.Email, Codes(role));
            else
                _logger.LogError(
                    "Demo account {Email} could not be given its role ({Errors}) and could not be removed ({DeleteErrors}). Disable it with the UserAdmin CLI.",
                    demo.Email, Codes(role), Codes(deleted));
            return null;
        }

        // A concurrent first login may have created it a moment ago (Identity's duplicate check or the unique
        // index): use that one. Otherwise the entry is unusable; log the codes, never the password.
        var raced = await users.FindByEmailAsync(email);
        if (raced is null)
        {
            _logger.LogWarning("Demo account {Email} could not be created: {Errors}.", demo.Email, Codes(created));
            return null;
        }

        // The winner adds the role in a second save that may not have committed yet, and removes the account if that
        // fails: until the role is there, refuse (a 401; the retry finds the finished account).
        return await users.IsInRoleAsync(raced, demo.Role) ? raced : null;
    }

    /// <summary>
    /// A write that throws (a <see cref="DbUpdateException"/> other than the insert race, or an
    /// <see cref="InvalidOperationException"/> for a role that doesn't exist) fails like any other: the login is
    /// refused, never a 500 (#275). Only the exception type is kept; its message can carry data.
    /// </summary>
    private static async Task<IdentityResult> FailureIfThrown(Func<Task<IdentityResult>> write)
    {
        try
        {
            return await write();
        }
        catch (Exception ex) when (ex is DbUpdateException or InvalidOperationException)
        {
            return IdentityResult.Failed(new IdentityError { Code = ex.GetType().Name });
        }
    }

    private static string Codes(IdentityResult result) => string.Join(", ", result.Errors.Select(e => e.Code));

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
            if (string.IsNullOrWhiteSpace(a.Email) || string.IsNullOrEmpty(a.Password) || !Roles.IsKnown(a.Role) || a.Expires == default)
            {
                logger.LogWarning("Skipping a demo account entry: it needs email, password, expires and a role of viewer or admin.");
                continue;
            }
            // Postgres timestamptz takes UTC only (Npgsql rejects other offsets). Portfolio ids are positive; others can't
            // match a portfolio, so they're dropped.
            result[a.Email] = a with
            {
                Expires = a.Expires.ToUniversalTime(),
                Portfolios = a.Portfolios is null ? null : [.. a.Portfolios.Where(id => id > 0).Distinct().Order()],
            };
        }
        return result;
    }
}

public sealed record DemoAccount(
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("expires")] DateTimeOffset Expires,
    [property: JsonPropertyName("portfolios")] int[]? Portfolios = null);

[JsonSerializable(typeof(DemoAccount[]))]
internal sealed partial class DemoAccountJson : JsonSerializerContext;
