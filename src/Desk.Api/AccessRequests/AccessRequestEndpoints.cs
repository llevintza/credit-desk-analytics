using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;
using Desk.Api.Audit;
using Desk.Api.Auth;
using Desk.Api.Limits;
using Desk.Data.App;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Desk.Api.AccessRequests;

/// <summary>What the request form sends (ADR-0029 §3, §4).</summary>
/// <param name="Email">Required, at most 254 characters; stored trimmed and lower case.</param>
/// <param name="Name">Optional, at most 500 characters.</param>
/// <param name="Purpose">Optional, at most 500 characters.</param>
/// <param name="Website">The honeypot: a hidden input a person leaves empty.</param>
/// <param name="StartedAt">When the form was rendered; a submit less than 3 s later is refused silently.</param>
public sealed record AccessRequestBody(string? Email, string? Name, string? Purpose, string? Website, DateTimeOffset? StartedAt);

/// <summary>The one answer every accepted-looking submit gets, so none can be told from another.</summary>
public sealed record AccessRequestReceived(string Status, string Detail);

/// <summary>
/// The anonymous demo access request (ADR-0029, #538). <c>POST /api/access-requests</c> stores a pending row and
/// <b>always</b> answers the same 202, for a new request, a duplicate of a pending one, an email that already has an
/// account, a filled honeypot or a too-fast submit, so it can't be used to find accounts; the 202 waits for the login
/// response-time floor (#230), so the time doesn't tell them apart either. <c>GET /api/access-requests</c> tells the
/// form whether the feature is on (204) without touching the database.
/// </summary>
/// <remarks>
/// Before any database call: maintenance mode (middleware), the per-IP window (rate limiter, 429), the honeypot, the
/// fill time and validation (400). The request holds a global database permit like every <c>/api</c> request. Logs
/// and the audit row carry the outcome, the status and the time, never the email or the name.
/// </remarks>
public static class AccessRequestEndpoints
{
    public const string Path = "/access-requests";
    public const string AuditEndpoint = "POST /api/access-requests";
    public const string LogCategory = "Desk.Api.AccessRequests";

    public static readonly AccessRequestReceived Received = new("received",
        "Thanks. If your request is approved, the site owner will send you the sign-in details.");

    /// <summary>
    /// Serialises the pending-cap count and the insert across instances and requests, so two requests can't both
    /// see room for one more. Held to the end of the transaction; any other advisory lock in this app uses another key.
    /// </summary>
    internal const long PendingCapLock = 0x5DE5_0538;

    public static RouteGroupBuilder MapAccessRequestEndpoints(this RouteGroupBuilder api, AccessRequestOptions options)
    {
        if (!options.Enabled)
        {
            // Off (the default): 404 for both, out of the OpenAPI document. Anonymous, so the group's authorization
            // doesn't turn the 404 into a 401, and handled here, so it never reaches the database.
            api.MapMethods(Path, [HttpMethods.Get, HttpMethods.Post], () => Results.NotFound())
                .AllowAnonymous()
                .WithMetadata(SkipRequestAudit.Instance)
                .ExcludeFromDescription();
            return api;
        }

        var group = api.MapGroup(Path).WithTags("Access requests").AllowAnonymous();

        group.MapPost("", SubmitAsync)
            .WithMetadata(SkipRequestAudit.Instance, LoginFloorMetadata.Accepted)
            .RequireRateLimiting(RateLimiting.AccessRequestPolicy)
            .WithName("RequestAccess")
            .WithSummary("Asks for a demo account. Always 202 with the same body, whether or not anything was stored.")
            .Produces<AccessRequestReceived>(StatusCodes.Status202Accepted)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        group.MapGet("", () => Results.NoContent())
            .WithMetadata(SkipRequestAudit.Instance)
            .WithName("GetAccessRequestsStatus")
            .WithSummary("204 when demo access requests are switched on (404 when off). Never touches the database.")
            .Produces(StatusCodes.Status204NoContent);

        return api;
    }

    internal static async Task<IResult> SubmitAsync(
        AccessRequestBody request,
        HttpContext http,
        AccessRequestOptions options,
        AppDbContext db,
        ILookupNormalizer normalizer,
        AuditQueue audit,
        TimeProvider time,
        ILoggerFactory loggers,
        CancellationToken ct)
    {
        var started = Stopwatch.GetTimestamp();
        var log = loggers.CreateLogger(LogCategory);
        var now = time.GetUtcNow();

        // Bots get the same 202 as everyone, and nothing is stored, audited or read.
        if (!string.IsNullOrEmpty(request.Website))
            return Accepted(log, "honeypot");
        if (request.StartedAt is not { } startedAt || now - startedAt < AccessRequestOptions.MinFillTime)
            return Accepted(log, "too_fast");

        var errors = Errors(request);
        if (errors.Count > 0)
        {
            log.LogInformation("Access request {Outcome}", "invalid");
            return Results.ValidationProblem(errors);
        }
        var valid = new Valid(NormalizeEmail(request.Email)!, Text(request.Name), Text(request.Purpose));

        string outcome;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({PendingCapLock})", ct);
            var pending = await db.AccessRequests.AsNoTracking().CountAsync(r => r.Status == AccessRequestStatus.Pending, ct);
            if (pending >= options.MaxPending)
            {
                await tx.RollbackAsync(ct);
                log.LogWarning("Access request {Outcome}: {Pending} pending, cap {Max}", "pending_cap", pending, options.MaxPending);
                audit.Enqueue(AuditKinds.AccessRequest, null, AuditEndpoint, StatusCodes.Status503ServiceUnavailable, started, time);
                http.Response.Headers.RetryAfter = AccessRequestOptions.PendingCapRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Requests are paused",
                    detail: "Too many requests are waiting for a decision. Please try again later.");
            }

            // An address that already has an account is answered like any other, and nothing is stored for it.
            var normalized = normalizer.NormalizeEmail(valid.Email);
            if (await db.Users.AsNoTracking().AnyAsync(u => u.NormalizedEmail == normalized, ct))
            {
                outcome = "existing_account";
            }
            else
            {
                // A second pending request for the address is dropped by the partial unique index, not an error.
                var inserted = await db.Database.ExecuteSqlAsync($"""
                    INSERT INTO app.access_request (email, display_name, purpose, status, created_at)
                    VALUES ({valid.Email}, {valid.Name}, {valid.Purpose}, 'pending', {now})
                    ON CONFLICT (lower(email)) WHERE status = 'pending' DO NOTHING
                    """, ct);
                outcome = inserted == 1 ? "stored" : "duplicate";
            }
            await tx.CommitAsync(ct);
        }

        audit.Enqueue(AuditKinds.AccessRequest, null, AuditEndpoint, StatusCodes.Status202Accepted, started, time);
        return Accepted(log, outcome);
    }

    private static IResult Accepted(ILogger log, string outcome)
    {
        log.LogInformation("Access request {Outcome}", outcome);
        return TypedResults.Accepted((string?)null, Received);
    }

    /// <summary>A request that passed the checks, normalised: the email trimmed and lower case, empty text as null.</summary>
    internal sealed record Valid(string Email, string? Name, string? Purpose);

    internal static Dictionary<string, string[]> Errors(AccessRequestBody request)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        if (!IsEmail(NormalizeEmail(request.Email)))
            errors["email"] = [$"Enter an email address of at most {AccessRequest.EmailMaxLength} characters."];
        if (Text(request.Name)?.Length > AccessRequest.TextMaxLength)
            errors["name"] = [$"At most {AccessRequest.TextMaxLength} characters."];
        if (Text(request.Purpose)?.Length > AccessRequest.TextMaxLength)
            errors["purpose"] = [$"At most {AccessRequest.TextMaxLength} characters."];
        return errors;
    }

    internal static string? NormalizeEmail(string? email) => email?.Trim().ToLowerInvariant();

    /// <summary>One plain address (no display name, no comments), as <see cref="MailAddress"/> reads it, within the column's limits.</summary>
    internal static bool IsEmail(string? email) =>
        email is { Length: >= 3 and <= AccessRequest.EmailMaxLength }
        && !email.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
        && MailAddress.TryCreate(email, out var parsed)
        && parsed.Address == email
        && parsed.DisplayName.Length == 0;

    private static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
