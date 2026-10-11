using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Desk.Api.Auth;

/// <summary>
/// Invite-only accounts on ASP.NET Core Identity bearer tokens (README §7.1, ADR-0024, which supersedes ADR-0005's
/// cookie session): the SPA may run on another site (GitHub Pages), where a cookie to the API would be third-party.
/// </summary>
public static class AuthSetup
{
    public static readonly string Scheme = IdentityConstants.BearerScheme;
    public const string AdminPolicy = "admin";
    /// <summary>Claim set at login and carried by every refresh: the 24 h absolute limit is measured from it.</summary>
    public const string AuthTimeClaim = "desk:auth_time";
    /// <summary>Claim set at login and carried by every refresh: one id per sign-in, so logout can revoke that session only.</summary>
    public const string SessionIdClaim = "desk:sid";

    /// <summary>
    /// The access token's life. Short, because a bearer token is checked without the database: a disable, password
    /// reset or account expiry takes effect at the next refresh, which this bounds (as the 5-minute stamp check did).
    /// </summary>
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(5);
    /// <summary>The refresh token's life, renewed by every refresh: the 8 h idle limit.</summary>
    public static readonly TimeSpan SlidingExpiry = TimeSpan.FromHours(8);
    public static readonly TimeSpan AbsoluteExpiry = TimeSpan.FromHours(24);

    public static IServiceCollection AddDeskAuth(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddIdentityCore<DeskUser>(IdentityPolicy.Apply)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddClaimsPrincipalFactory<DeskClaimsFactory>()
            // Lockout on the injected clock, like the token expiry and session checks below (#261).
            .AddUserManager<DeskUserManager>()
            .AddSignInManager<DeskSignInManager>();
        // Scoped like the hasher it verifies through, so a test's counting hasher sees the decoy checks too.
        services.AddScoped<TimingGuard>();
        services.AddSingleton(LoginFloorOptions.Default);
        services.AddSingleton<SessionRevocations>();

        services.AddAuthentication(Scheme).AddBearerToken(Scheme, o =>
        {
            o.BearerTokenExpiration = AccessTokenLifetime;
            o.RefreshTokenExpiration = SlidingExpiry;
        });
        // One clock for token expiry, the session limits and account expiry (tests swap in a fake).
        services.AddOptions<BearerTokenOptions>(Scheme).Configure<TimeProvider>((o, time) => o.TimeProvider = time);

        // The /api group requires a signed-in user by default; only login and refresh opt out (README §7).
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

        // Keys in the database, so tokens survive restarts and redeploys (README §7.1).
        services.AddDataProtection()
            .SetApplicationName("credit-desk-analytics")
            .PersistKeysToDbContext<AppDbContext>();
        // Data protection preloads the key ring in a hosted service at startup. With keys in the database that
        // would open a connection on every boot (waking Neon, and contradicting maintenance mode). Without it the
        // ring loads on the first protect/unprotect, i.e. the first login or token check.
        // Looked up by name (it's internal); throwOnError makes a rename fail every test instead of silently waking the DB.
        var preload = typeof(DataProtectionOptions).Assembly.GetType(
            "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService", throwOnError: true);
        foreach (var d in services.Where(d => d.ImplementationType == preload).ToList())
            services.Remove(d);

        return services;
    }
}
