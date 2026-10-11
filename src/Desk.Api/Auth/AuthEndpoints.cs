using System.Diagnostics;
using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using Desk.Api.Audit;
using Desk.Api.Limits;
using Desk.Data.App;
using Desk.Data.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.BearerToken;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Desk.Api.Auth;

public static class AuthEndpoints
{
    /// <summary>Same message for every failure (unknown email, wrong password, locked, expired, disabled): no account probing.</summary>
    public const string LoginFailedDetail = "Invalid email or password, or the account is locked, disabled or expired.";
    /// <summary>Same message for every refresh failure (bad, expired or logged-out token, disabled, reset or expired account).</summary>
    public const string RefreshFailedDetail = "The session has ended. Sign in again.";

    public static RouteGroupBuilder MapAuthEndpoints(this RouteGroupBuilder api)
    {
        var auth = api.MapGroup("/auth").WithTags("Account");

        auth.MapPost("/login", LoginAsync)
            .AllowAnonymous()
            .WithMetadata(SkipRequestAudit.Instance, LoginFloorMetadata.Instance)
            .RequireRateLimiting(RateLimiting.LoginPolicy)
            .WithName("Login")
            .WithSummary("Signs in with email and password and returns a bearer access token and a refresh token.")
            .Produces<AccessTokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/refresh", RefreshAsync)
            .AllowAnonymous()
            .WithName("RefreshToken")
            .WithSummary("Swaps a refresh token for a new access token and refresh token, while the session is still valid.")
            .Produces<AccessTokenResponse>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);

        auth.MapPost("/logout", Logout)
            .WithName("Logout")
            .WithSummary("Ends this sign-in: its access and refresh tokens stop working on this instance.")
            .Produces(StatusCodes.Status204NoContent);

        api.MapGet("/me", GetMe)
            .WithTags("Account")
            .WithName("GetCurrentUser")
            .WithSummary("Current user, roles and account expiry.")
            .Produces<MeResponse>();

        return api;
    }

    internal static async Task<IResult> LoginAsync(
        LoginRequest request,
        HttpContext http,
        SignInManager<DeskUser> signIn,
        DemoAccounts demo,
        AuditQueue audit,
        TimingGuard guard,
        AppDbContext db,
        TimeProvider time,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var email = request.Email?.Trim() ?? "";

        var password = request.Password ?? "";
        var user = email.Length == 0 ? null : await demo.FindOrCreateAsync(email, signIn.UserManager, db, ct);

        // Exactly one PBKDF2 verification per attempt on every path (R105-F2): the decoy here for an unknown email, and
        // DeskSignInManager for the rest (wrong or empty password, locked, disabled, expired, and the attempt that
        // triggers lockout). The DB work still differs per path; LoginFloor pads every 401 to the same time (#230).
        SignInResult result;
        if (user is null)
        {
            guard.Verify(password);
            result = SignInResult.Failed;
        }
        else
        {
            result = await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true);
        }

        if (!result.Succeeded)
        {
            audit.Enqueue(AuditKinds.LoginFailure, email, "/api/auth/login", StatusCodes.Status401Unauthorized, started, time);
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Login failed", detail: LoginFailedDetail);
        }

        var principal = await signIn.CreateUserPrincipalAsync(user!);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(AuthSetup.AuthTimeClaim, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(AuthSetup.SessionIdClaim, Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

        audit.Enqueue(AuditKinds.LoginSuccess, user!.Email, "/api/auth/login", StatusCodes.Status200OK, started, time);
        // The bearer handler writes the AccessTokenResponse body itself.
        await http.SignInAsync(AuthSetup.Scheme, principal);
        return Results.Empty;
    }

    /// <summary>
    /// Identity's refresh (as <c>MapIdentityApi</c> does it), plus this app's session rules: the token must be
    /// unexpired (8 h idle), the session under 24 h and not logged out, the security stamp unchanged (disable,
    /// password reset), and the account still allowed to sign in (not disabled or expired). The new tokens keep the
    /// original sign-in time and session id, so neither limit can be extended by refreshing.
    /// </summary>
    internal static async Task<IResult> RefreshAsync(
        RefreshRequest request,
        HttpContext http,
        SignInManager<DeskUser> signIn,
        IOptionsMonitor<BearerTokenOptions> bearer,
        SessionRevocations revoked,
        TimeProvider time)
    {
        var now = time.GetUtcNow();
        var ticket = string.IsNullOrEmpty(request.RefreshToken) ? null : bearer.Get(AuthSetup.Scheme).RefreshTokenProtector.Unprotect(request.RefreshToken);
        if (ticket?.Properties.ExpiresUtc is not { } expires || now >= expires || !SessionLimits.IsLive(ticket.Principal, now, revoked)
            || await signIn.ValidateSecurityStampAsync(ticket.Principal) is not { } user
            || !await signIn.CanSignInAsync(user))
        {
            return Results.Problem(statusCode: StatusCodes.Status401Unauthorized, title: "Refresh failed", detail: RefreshFailedDetail);
        }

        var principal = await signIn.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(ticket.Principal.FindFirst(AuthSetup.AuthTimeClaim)!.Clone());
        identity.AddClaim(ticket.Principal.FindFirst(AuthSetup.SessionIdClaim)!.Clone());
        await http.SignInAsync(AuthSetup.Scheme, principal);
        return Results.Empty;
    }

    internal static IResult Logout(HttpContext http, SessionRevocations revoked, TimeProvider time)
    {
        // Signed in here means SessionLimits found both claims valid.
        var sid = http.User.FindFirstValue(AuthSetup.SessionIdClaim)!;
        var authTime = SessionLimits.ReadAuthTime(http.User)!.Value;
        revoked.Revoke(sid, authTime + AuthSetup.AbsoluteExpiry, time.GetUtcNow());
        return Results.NoContent();
    }

    internal static IResult GetMe(HttpContext http)
    {
        var user = http.User;
        var roles = user.Claims.Where(c => c.Type == user.Identities.First().RoleClaimType).Select(c => c.Value).Order().ToArray();
        return Results.Ok(new MeResponse(user.Identity!.Name!, roles, DeskClaimsFactory.ReadExpiry(user)!.Value));
    }
}

public sealed record LoginRequest(string? Email, string? Password);

public sealed record RefreshRequest(string? RefreshToken);

public sealed record MeResponse(string Email, string[] Roles, DateTimeOffset ExpiresAt);
