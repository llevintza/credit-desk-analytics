using Desk.Api.Limits;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Desk.Api.Auth;

/// <summary>
/// Invite-only accounts on a same-origin cookie session (README §7.1, ADR-0005).
/// </summary>
public static class AuthSetup
{
    public const string CookieName = "__Host-desk";
    /// <summary>
    /// The antiforgery cookie behind the TLS proxy outside Development (production): <c>__Host-</c> prefix and
    /// always Secure, so nothing on another subdomain or plain HTTP can plant or overwrite it (#118 N1).
    /// </summary>
    public const string AntiforgeryCookieName = "__Host-desk-af";
    /// <summary>
    /// The antiforgery cookie off the proxy (local compose, CI e2e and budgets, the dev proxy), which serve plain
    /// HTTP: Secure only when the request is HTTPS, so no <c>__Host-</c> prefix. Antiforgery refuses to issue an
    /// always-Secure cookie on a plain-HTTP request, which was a login 500 on compose.
    /// </summary>
    public const string PlainHttpAntiforgeryCookieName = "desk-af";
    /// <summary>Readable by the SPA, echoed back in <see cref="AntiforgeryHeaderName"/> (Angular's default names).</summary>
    public const string XsrfCookieName = "XSRF-TOKEN";
    public const string AntiforgeryHeaderName = "X-XSRF-TOKEN";
    public const string AdminPolicy = "admin";
    /// <summary>Stored in the ticket at login; the absolute session limit is measured from it, not from the last renewal.</summary>
    public const string AuthTimeKey = "desk.auth_time";

    public static readonly TimeSpan SlidingExpiry = TimeSpan.FromHours(8);
    public static readonly TimeSpan AbsoluteExpiry = TimeSpan.FromHours(24);
    public static readonly TimeSpan SecurityStampInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Every request is HTTPS: behind Render's TLS proxy (it redirects plain HTTP, and the app reads
    /// <c>X-Forwarded-Proto</c>), outside Development. Local compose and CI run Production over plain HTTP without the
    /// proxy setting, so they stay on the plain-HTTP antiforgery cookie.
    /// </summary>
    public static bool HttpsOnly(IConfiguration config, IHostEnvironment env) =>
        !env.IsDevelopment() && ClientAddress.BehindProxy(config);

    /// <param name="services">The app's services.</param>
    /// <param name="httpsOnly"><see cref="HttpsOnly"/>: issue the <c>__Host-</c>, always-Secure antiforgery cookie.</param>
    public static IServiceCollection AddDeskAuth(this IServiceCollection services, bool httpsOnly)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddIdentityCore<DeskUser>(IdentityPolicy.Apply)
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<AppDbContext>()
            .AddClaimsPrincipalFactory<DeskClaimsFactory>()
            .AddSignInManager<DeskSignInManager>();
        // Scoped like the hasher it verifies through, so a test's counting hasher sees the decoy checks too.
        services.AddScoped<TimingGuard>();

        services.AddAuthentication(IdentityConstants.ApplicationScheme)
            .AddCookie(IdentityConstants.ApplicationScheme, ConfigureCookie)
            // Never issued (no 2FA), but Identity's security-stamp validator signs it out when it rejects a session.
            .AddCookie(IdentityConstants.TwoFactorRememberMeScheme, o =>
            {
                o.Cookie.Name = "__Host-desk-2fa";
                o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                o.Cookie.SameSite = SameSiteMode.Strict;
            });
        // Identity's security-stamp validator: a disable or password reset ends live sessions within the interval.
        services.AddOptions<SecurityStampValidatorOptions>()
            .Configure<TimeProvider>((o, time) => { o.ValidationInterval = SecurityStampInterval; o.TimeProvider = time; });
        // One clock for sliding expiry, the absolute limit and account expiry (tests swap in a fake).
        services.AddOptions<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme)
            .Configure<TimeProvider>((o, time) => o.TimeProvider = time);

        services.AddScoped<ISecurityStampValidator, SecurityStampValidator<DeskUser>>();

        // The /api group requires a session by default; only login opts out (README §7).
        services.AddAuthorizationBuilder()
            .AddPolicy(AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

        services.AddAntiforgery(o =>
        {
            o.HeaderName = AntiforgeryHeaderName;
            o.Cookie.Name = httpsOnly ? AntiforgeryCookieName : PlainHttpAntiforgeryCookieName;
            o.Cookie.SecurePolicy = httpsOnly ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.HttpOnly = true;
        });

        // Keys in the database, so sessions survive restarts and redeploys (README §7.1).
        services.AddDataProtection()
            .SetApplicationName("credit-desk-analytics")
            .PersistKeysToDbContext<AppDbContext>();
        // Data protection preloads the key ring in a hosted service at startup. With keys in the database that
        // would open a connection on every boot (waking Neon, and contradicting maintenance mode). Without it the
        // ring loads on the first protect/unprotect, i.e. the first login or session check.
        // Looked up by name (it's internal); throwOnError makes a rename fail every test instead of silently waking the DB.
        var preload = typeof(DataProtectionOptions).Assembly.GetType(
            "Microsoft.AspNetCore.DataProtection.Internal.DataProtectionHostedService", throwOnError: true);
        foreach (var d in services.Where(d => d.ImplementationType == preload).ToList())
            services.Remove(d);

        return services;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions o)
    {
        o.Cookie.Name = CookieName;
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.Cookie.Path = "/";
        o.ExpireTimeSpan = SlidingExpiry;
        o.SlidingExpiration = true;

        // An API never redirects to a login page: the SPA reads the status code.
        o.Events.OnRedirectToLogin = ctx => { ctx.Response.StatusCode = StatusCodes.Status401Unauthorized; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = ctx => { ctx.Response.StatusCode = StatusCodes.Status403Forbidden; return Task.CompletedTask; };
        o.Events.OnValidatePrincipal = ValidatePrincipalAsync;
    }

    /// <summary>
    /// Ends the session at the 24 h absolute limit or at the account's expiry, then runs Identity's
    /// security-stamp check (disable / reset).
    /// </summary>
    internal static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext ctx)
    {
        var now = ctx.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow();
        var expiry = DeskClaimsFactory.ReadExpiry(ctx.Principal!);
        if (!ctx.Properties.Items.TryGetValue(AuthTimeKey, out var raw)
            || !DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var authTime)
            || now - authTime >= AbsoluteExpiry
            || expiry is null
            || now >= expiry)
        {
            ctx.RejectPrincipal();
            await ctx.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        await SecurityStampValidator.ValidatePrincipalAsync(ctx);
    }
}
