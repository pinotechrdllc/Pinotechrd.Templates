using System.ComponentModel.DataAnnotations;

namespace MyApp.BuildingBlocks.Web.Security;

/// <summary>
/// The global rate limit, bound from <see cref="SectionName"/>: each client (see
/// <see cref="HttpRateLimiting.ClientPartitionKey"/>) may send <see cref="PermitLimit"/> requests per fixed
/// <see cref="Window"/>; further requests answer 429 until the window ends.
/// </summary>
public sealed class HttpRateLimitOptions : IValidatableObject
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Http:RateLimiting";

    /// <summary>
    /// The largest <see cref="PermitLimit"/>, 10,000: no person or browser sends more within one window, so a higher
    /// value would only switch the limiter off.
    /// </summary>
    public const int MaximumPermitLimit = 10_000;

    /// <summary>
    /// The highest average rate a limit may allow, 100 requests per second per client: above it a short window with a
    /// high limit no longer slows anyone down, which also switches the limiter off.
    /// </summary>
    public const int MaximumRequestsPerSecond = 100;

    /// <summary>
    /// The requests a client may send per <see cref="Window"/>. 300 by default (5 per second on average): generous
    /// for a person using the application, low enough to slow down a scripted client. It must be between 1 and
    /// <see cref="MaximumPermitLimit"/>.
    /// </summary>
    [Range(1, MaximumPermitLimit, ErrorMessage = SectionName + ":" + nameof(PermitLimit) + " must be between 1 and 10000 requests.")]
    public int PermitLimit { get; set; } = 300;

    /// <summary>
    /// The length of a window, one minute by default. It must be between one second and one hour: a rejected client
    /// may have to wait for the rest of the window, and a longer one would turn a burst into a lockout.
    /// </summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00", ErrorMessage = SectionName + ":" + nameof(Window) + " must be between 00:00:01 and 01:00:00.")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The stricter limit of the sign-in, refresh, logout and password change endpoints
    /// (<see cref="HttpRateLimiting.AuthPolicy"/>), bound from <c>Http:RateLimiting:Auth</c>, on top of this one.
    /// </summary>
    public HttpAuthRateLimitOptions Auth { get; set; } = new();

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (TooFast(PermitLimit, Window) is { } global)
        {
            yield return new ValidationResult(global, [nameof(PermitLimit), nameof(Window)]);
        }

        // Data annotations do not reach into a nested object: the auth limit is checked here, under its own names.
        var auth = Auth ?? new HttpAuthRateLimitOptions();
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(auth, new ValidationContext(auth), results, validateAllProperties: true);
        foreach (var result in results)
        {
            yield return new ValidationResult(result.ErrorMessage, [nameof(Auth)]);
        }

        if (results.Count == 0 && TooFast(auth.PermitLimit, auth.Window, HttpAuthRateLimitOptions.SectionName) is { } strict)
        {
            yield return new ValidationResult(strict, [nameof(Auth)]);
        }
    }

    private static string? TooFast(int permitLimit, TimeSpan window, string section = SectionName) =>
        window > TimeSpan.Zero && permitLimit / window.TotalSeconds > MaximumRequestsPerSecond
            ? $"{section}:{nameof(PermitLimit)} {permitLimit} per {section}:{nameof(Window)} {window} allows more than "
                + $"{MaximumRequestsPerSecond} requests per second on average, which no longer limits anyone; lower the limit or lengthen the window."
            : null;
}

/// <summary>
/// The limit of the authentication endpoints, bound from <see cref="SectionName"/>: each client (see
/// <see cref="HttpRateLimiting.ClientPartitionKey"/>) may send <see cref="PermitLimit"/> of those requests per fixed
/// <see cref="Window"/>, counted together and on top of the global limit. It slows password guessing from one address;
/// the account lockout covers guessing spread over many.
/// </summary>
public sealed class HttpAuthRateLimitOptions
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = HttpRateLimitOptions.SectionName + ":Auth";

    /// <summary>
    /// The requests a client may send per <see cref="Window"/>, 10 by default: enough for a person who mistypes a password
    /// a few times, few for a script. Between 1 and <see cref="HttpRateLimitOptions.MaximumPermitLimit"/>.
    /// </summary>
    [Range(1, HttpRateLimitOptions.MaximumPermitLimit, ErrorMessage = SectionName + ":" + nameof(PermitLimit) + " must be between 1 and 10000 requests.")]
    public int PermitLimit { get; set; } = 10;

    /// <summary>The length of a window, one minute by default; between one second and one hour.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00", ErrorMessage = SectionName + ":" + nameof(Window) + " must be between 00:00:01 and 01:00:00.")]
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
}
