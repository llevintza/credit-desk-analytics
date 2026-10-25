namespace Desk.Api.AccessRequests;

/// <summary>
/// The demo access-request queue's settings (ADR-0029 §2, §3), read once at start-up: the endpoints are mapped, or
/// left out of the OpenAPI document, from <see cref="Enabled"/>.
/// </summary>
public sealed class AccessRequestOptions
{
    public const string EnabledConfigKey = "ACCESS_REQUESTS_ENABLED";
    public const string MaxPendingConfigKey = "ACCESS_REQUESTS_MAX_PENDING";
    public const int DefaultMaxPending = 200;
    public const int MaxMaxPending = 1_000;
    /// <summary>ADR-0029 §3: a submit sooner than this after the form rendered is a bot's.</summary>
    public static readonly TimeSpan MinFillTime = TimeSpan.FromSeconds(3);
    /// <summary>ADR-0029 §3: the pending cap's 503 says to come back in an hour.</summary>
    public const int PendingCapRetryAfterSeconds = 3600;

    public AccessRequestOptions(IConfiguration config, ILogger<AccessRequestOptions> logger)
    {
        // Off unless exactly a boolean true (the MAINTENANCE_MODE rule): stored emails need the purge first (#542).
        Enabled = bool.TryParse(config[EnabledConfigKey], out var on) && on;
        var value = config[MaxPendingConfigKey];
        MaxPending = DefaultMaxPending;
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (int.TryParse(value, out var max) && max is >= 1 and <= MaxMaxPending)
                MaxPending = max;
            else
                logger.LogWarning("{Key} must be a whole number from 1 to {Max}; keeping the default of {Default}.",
                    MaxPendingConfigKey, MaxMaxPending, DefaultMaxPending);
        }
    }

    public bool Enabled { get; }

    /// <summary>Pending requests at which a new one gets 503 instead of being stored.</summary>
    public int MaxPending { get; }
}
