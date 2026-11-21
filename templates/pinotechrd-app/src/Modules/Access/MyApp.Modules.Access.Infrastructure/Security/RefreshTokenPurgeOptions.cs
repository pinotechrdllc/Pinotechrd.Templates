using System.Globalization;
using Microsoft.Extensions.Options;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The settings of <see cref="RefreshTokenPurger"/>, in <see cref="SectionName"/>: which refresh token families are
/// deleted, and how gently. A family is purged once its session ended (<c>absolute_expires_at</c>) or it was revoked,
/// <see cref="GracePeriod"/> ago; its tokens go with it.
/// </summary>
public sealed class RefreshTokenPurgeOptions
{
    /// <summary>The configuration section.</summary>
    public const string SectionName = RefreshTokenOptions.SectionName + ":Purge";

    /// <summary>Gets or sets whether <see cref="RefreshTokenPurgeService"/> purges on a schedule. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Gets or sets the time between two rounds. At least 1 minute, at most 1 day. Default 1 hour.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Gets or sets the random share of <see cref="Interval"/> added or removed from each wait, so instances drift apart. From 0 to 0.5. Default 0.10.</summary>
    public double Jitter { get; set; } = 0.10;

    /// <summary>Gets or sets the longest random wait before the first round, so instances started together do not align. From 0 to 1 hour. Default 1 minute.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets or sets how long a family is kept after it expired or was revoked. At least 1 hour. Default 7 days.</summary>
    public TimeSpan GracePeriod { get; set; } = TimeSpan.FromDays(7);

    /// <summary>Gets or sets how many families one delete statement removes. From 1 to 1000. Default 100.</summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>Gets or sets the database's time limit for each statement of a batch. At least 100 milliseconds, at most 10 minutes. Default 30 seconds.</summary>
    public TimeSpan StatementTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets how long a batch waits for a row lock held by another transaction (<c>lock_timeout</c>) before it is
    /// abandoned. At least 100 milliseconds and at most <see cref="StatementTimeout"/>. Default 5 seconds.
    /// </summary>
    public TimeSpan LockTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets how many batches one purge of a tenant runs at most; the rest is left for the next purge. At least 1. Default 50.</summary>
    public int MaxBatchesPerTenant { get; set; } = 50;
}

/// <summary>Stops the host at start when a <see cref="RefreshTokenPurgeOptions"/> value is outside its range.</summary>
public sealed class RefreshTokenPurgeOptionsValidator : IValidateOptions<RefreshTokenPurgeOptions>
{
    private const string Prefix = RefreshTokenPurgeOptions.SectionName + ":";

    private static readonly TimeSpan MinimumTimeout = TimeSpan.FromMilliseconds(100);

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RefreshTokenPurgeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        if (options.GracePeriod < TimeSpan.FromHours(1))
        {
            failures.Add(Describe(nameof(RefreshTokenPurgeOptions.GracePeriod), options.GracePeriod.ToString("c", CultureInfo.InvariantCulture), "at least 1 hour"));
        }

        if (options.BatchSize is < 1 or > 1000)
        {
            failures.Add(Describe(nameof(RefreshTokenPurgeOptions.BatchSize), options.BatchSize.ToString(CultureInfo.InvariantCulture), "between 1 and 1000"));
        }

        if (options.StatementTimeout < MinimumTimeout || options.StatementTimeout > TimeSpan.FromMinutes(10))
        {
            failures.Add(Describe(nameof(RefreshTokenPurgeOptions.StatementTimeout), options.StatementTimeout.ToString("c", CultureInfo.InvariantCulture), "between 100 milliseconds and 10 minutes"));
        }

        if (options.LockTimeout < MinimumTimeout || (options.StatementTimeout > TimeSpan.Zero && options.LockTimeout > options.StatementTimeout))
        {
            failures.Add(Describe(nameof(RefreshTokenPurgeOptions.LockTimeout), options.LockTimeout.ToString("c", CultureInfo.InvariantCulture), "at least 100 milliseconds and at most " + nameof(RefreshTokenPurgeOptions.StatementTimeout)));
        }

        if (options.MaxBatchesPerTenant < 1)
        {
            failures.Add(Describe(nameof(RefreshTokenPurgeOptions.MaxBatchesPerTenant), options.MaxBatchesPerTenant.ToString(CultureInfo.InvariantCulture), "at least 1"));
        }

        if (options.Interval < TimeSpan.FromMinutes(1) || options.Interval > TimeSpan.FromDays(1))
        {
            failures.Add(Describe(nameof(RefreshTokenPurgeOptions.Interval), options.Interval.ToString("c", CultureInfo.InvariantCulture), "between 1 minute and 1 day"));
        }

        if (double.IsNaN(options.Jitter) || options.Jitter is < 0 or > 0.5)
        {
            failures.Add(Describe(nameof(RefreshTokenPurgeOptions.Jitter), options.Jitter.ToString(CultureInfo.InvariantCulture), "between 0 and 0.5"));
        }

        if (options.InitialDelay < TimeSpan.Zero || options.InitialDelay > TimeSpan.FromHours(1))
        {
            failures.Add(Describe(nameof(RefreshTokenPurgeOptions.InitialDelay), options.InitialDelay.ToString("c", CultureInfo.InvariantCulture), "between 0 and 1 hour"));
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static string Describe(string property, string value, string rule) =>
        string.Create(CultureInfo.InvariantCulture, $"{Prefix}{property} is {value}; it must be {rule}.");
}
