using System.Globalization;
using Microsoft.Extensions.Options;
using Pinotechrd.Security.Options;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The settings of <see cref="DatabaseRefreshTokenStore"/>, in <see cref="SectionName"/>. Each rotation gives a token the
/// sliding lifetime of the security library (<c>Access:Jwt:RefreshTokenExpiryDays</c>); <see cref="AbsoluteLifetime"/>
/// caps the whole session, which the library does not.
/// </summary>
public sealed class RefreshTokenOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = "Access:RefreshTokens";

    /// <summary>
    /// Gets or sets how long a sign-in session lives at most, from its first token, however often it is refreshed.
    /// Default 30 days.
    /// </summary>
    public TimeSpan AbsoluteLifetime { get; set; } = TimeSpan.FromDays(30);
}

/// <summary>
/// Stops the host at start when the sliding lifetime of a refresh token (<c>Access:Jwt:RefreshTokenExpiryDays</c>) is not
/// positive, or when <see cref="RefreshTokenOptions.AbsoluteLifetime"/> is shorter than it: every session would then end
/// before its first token, silently cut short.
/// </summary>
/// <param name="security">The security library's settings, for the sliding lifetime.</param>
public sealed class RefreshTokenOptionsValidator(IOptions<PinotechrdSecurityOptions> security) : IValidateOptions<RefreshTokenOptions>
{
    private const string SlidingSetting = "Access:Jwt:RefreshTokenExpiryDays";
    private const string AbsoluteSetting = RefreshTokenOptions.SectionName + ":" + nameof(RefreshTokenOptions.AbsoluteLifetime);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RefreshTokenOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var slidingDays = security.Value.Jwt.RefreshTokenExpiryDays;
        if (slidingDays <= 0)
        {
            return ValidateOptionsResult.Fail(
                string.Create(CultureInfo.InvariantCulture, $"{SlidingSetting} is {slidingDays}; it must be at least 1 day."));
        }

        return options.AbsoluteLifetime >= TimeSpan.FromDays(slidingDays)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(string.Create(
                CultureInfo.InvariantCulture,
                $"{AbsoluteSetting} is {options.AbsoluteLifetime:c}; it must not be shorter than {SlidingSetting} ({slidingDays} days)."));
    }
}
