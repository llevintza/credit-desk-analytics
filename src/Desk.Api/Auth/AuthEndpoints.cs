using System.Diagnostics;
using System.Globalization;
using Desk.Api.Audit;
using Desk.Api.Limits;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;

namespace Desk.Api.Auth;

public static class AuthEndpoints
{
    /// <summary>Same message for every failure (unknown email, wrong password, locked, expired, disabled): no account probing.</summary>
    public const string LoginFailedDetail = "Invalid email or password, or the account is locked, disabled or expired.";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Account");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithMetadata(SkipAntiforgery.Instance, SkipRequestAudit.Instance)
            .RequireRateLimiting(RateLimiting.LoginPolicy)
            .WithName("Login")
            .WithSummary("Signs in with email and password and sets the session and XSRF-TOKEN cookies.")
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        auth.MapPost("/logout", (Func<HttpContext, Task<IResult>>)LogoutAsync)
            .WithName("Logout")
            .WithSummary("Ends the session. Needs the X-XSRF-TOKEN header.")
            .Produces(StatusCodes.Status204NoContent);

        auth.MapGet("/antiforgery", IssueAntiforgery)
            .WithName("GetAntiforgeryToken")
            .WithSummary("Refreshes the XSRF-TOKEN cookie that state-changing requests echo in X-XSRF-TOKEN.")
            .Produces(StatusCodes.Status204NoContent);

        api.MapGet("/me", GetMe)
            .WithTags("Account")
            .WithName("GetCurrentUser")
            .WithSummary("Current user, roles and account expiry.")
            .Produces<MeResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        return api;
    }

    internal static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        SignInManager<DeskUser> signIn,
        DemoAccounts demo,
        IAntiforgery antiforgery,
        AuditQueue audit,
        TimeProvider time,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var email = request.Email?.Trim() ?? "";

        if (email.Length > 0)
            await demo.EnsureAsync(email, signIn.UserManager, ct);

        var user = email.Length == 0 ? null : await signIn.UserManager.FindByEmailAsync(email);
        var result = user is null || string.IsNullOrEmpty(request.Password)
            ? SignInResult.Failed
            : await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);

        if (!result.Succeeded)
        {
            audit.Enqueue(AuditKinds.LoginFailure, email, "/api/auth/login", StatusCodes.Status401Unauthorized, started, time);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Login failed", detail: LoginFailedDetail);
        }

        var principal = await signIn.CreateUserPrincipalAsync(user!);
        var props = new AuthenticationProperties { IsPersistent = false };
        props.Items[AuthSetup.AuthTimeKey] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        await http.SignInAsync(IdentityConstants.ApplicationScheme, principal, props);

        // Antiforgery tokens are bound to the identity: issue one for the new session.
        http.User = principal;
        AppendXsrfCookie(http, antiforgery);

        audit.Enqueue(AuditKinds.LoginSuccess, user!.Email, "/api/auth/login", StatusCodes.Status200OK, started, time);
        return Results.Ok(new MeResponse(user.Email!, [.. await signIn.UserManager.GetRolesAsync(user)], user.ExpiresAt));
    }

    internal static async Task<IResult> LogoutAsync(HttpContext http)
    {
        await http.SignOutAsync(IdentityConstants.ApplicationScheme);
        http.Response.Cookies.Delete(AuthSetup.XsrfCookieName, new CookieOptions { Path = "/", Secure = true, SameSite = SameSiteMode.Strict });
        return Results.NoContent();
    }

    internal static IResult IssueAntiforgery(HttpContext http, IAntiforgery antiforgery)
    {
        AppendXsrfCookie(http, antiforgery);
        return Results.NoContent();
    }

    internal static IResult GetMe(HttpContext http)
    {
        var user = http.User;
        var roles = user.Claims.Where(c => c.Type == user.Identities.First().RoleClaimType).Select(c => c.Value).Order().ToArray();
        return Results.Ok(new MeResponse(user.Identity!.Name!, roles, DeskClaimsFactory.ReadExpiry(user)!.Value));
    }

    private static void AppendXsrfCookie(HttpContext http, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(AuthSetup.XsrfCookieName, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false, // the SPA reads it and echoes it in X-XSRF-TOKEN
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }
}

public sealed record LoginRequest(string? Email, string? Password);

public sealed record MeResponse(string Email, string[] Roles, DateTimeOffset ExpiresAt);
