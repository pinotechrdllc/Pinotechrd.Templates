using MyApp.Api.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Security;

namespace MyApp.Api.Tests.Access;

// The settings of the refresh token purger: every value outside its range is refused with the setting's full name, all
// failures together, and the host does not start with one.
public class RefreshTokenPurgeOptionsTests
{
    private const string Prefix = RefreshTokenPurgeOptions.SectionName + ":";

    private static readonly RefreshTokenPurgeOptionsValidator Validator = new();

    private static RefreshTokenPurgeOptions With(string property, string value)
    {
        var options = new RefreshTokenPurgeOptions();
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        switch (property)
        {
            case nameof(RefreshTokenPurgeOptions.GracePeriod): options.GracePeriod = TimeSpan.Parse(value, invariant); break;
            case nameof(RefreshTokenPurgeOptions.BatchSize): options.BatchSize = int.Parse(value, invariant); break;
            case nameof(RefreshTokenPurgeOptions.StatementTimeout): options.StatementTimeout = TimeSpan.Parse(value, invariant); break;
            case nameof(RefreshTokenPurgeOptions.LockTimeout): options.LockTimeout = TimeSpan.Parse(value, invariant); break;
            case nameof(RefreshTokenPurgeOptions.MaxBatchesPerTenant): options.MaxBatchesPerTenant = int.Parse(value, invariant); break;
            default: throw new ArgumentOutOfRangeException(nameof(property), property, null);
        }

        return options;
    }

    // Breaks when the defaults are refused: a host with no Purge section must start.
    [Fact]
    public void TheDefaultsAreValid()
    {
        Assert.True(Validator.Validate(null, new RefreshTokenPurgeOptions()).Succeeded);
    }

    // Breaks when a value outside its range is accepted, or the failure does not name the setting.
    [Theory]
    [InlineData("GracePeriod", "00:59:59")]
    [InlineData("GracePeriod", "-1.00:00:00")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "1001")]
    [InlineData("StatementTimeout", "00:00:00")]
    [InlineData("StatementTimeout", "-00:00:01")]
    [InlineData("StatementTimeout", "00:10:00.001")]
    [InlineData("LockTimeout", "00:00:00")]
    [InlineData("LockTimeout", "00:00:00.099")]
    [InlineData("LockTimeout", "-00:00:01")]
    [InlineData("LockTimeout", "00:00:30.001")]
    [InlineData("MaxBatchesPerTenant", "0")]
    public void AValueOutsideItsRangeIsRefusedNamingTheSetting(string property, string value)
    {
        var result = Validator.Validate(null, With(property, value));

        Assert.True(result.Failed);
        var failure = Assert.Single(result.Failures!);
        Assert.Contains(Prefix + property, failure, StringComparison.Ordinal);
    }

    // Breaks when a boundary value is refused (the ranges are inclusive where documented).
    [Theory]
    [InlineData("GracePeriod", "01:00:00")]
    [InlineData("BatchSize", "1")]
    [InlineData("BatchSize", "1000")]
    [InlineData("StatementTimeout", "00:10:00")]
    [InlineData("MaxBatchesPerTenant", "1")]
    public void ABoundaryValueIsAccepted(string property, string value) =>
        Assert.True(Validator.Validate(null, With(property, value)).Succeeded);

    // Breaks when the StatementTimeout bounds or the LockTimeout <= StatementTimeout rule are off by one: equal values
    // at both ends of the StatementTimeout range are accepted.
    [Theory]
    [InlineData("00:00:00.100", "00:00:00.100")]
    [InlineData("00:10:00", "00:10:00")]
    [InlineData("00:10:00", "00:00:05")]
    public void AStatementTimeoutBoundaryWithALockTimeoutNotAboveItIsAccepted(string statement, string lockTimeout)
    {
        var invariant = System.Globalization.CultureInfo.InvariantCulture;
        var options = new RefreshTokenPurgeOptions { StatementTimeout = TimeSpan.Parse(statement, invariant), LockTimeout = TimeSpan.Parse(lockTimeout, invariant) };

        Assert.True(Validator.Validate(null, options).Succeeded);
    }

    // Breaks when a LockTimeout above a non-default StatementTimeout is accepted, or the failure names the wrong setting.
    [Fact]
    public void ALockTimeoutAboveTheStatementTimeoutIsRefusedNamingTheLockTimeout()
    {
        var options = new RefreshTokenPurgeOptions { StatementTimeout = TimeSpan.FromSeconds(1), LockTimeout = TimeSpan.FromMilliseconds(1001) };

        var failure = Assert.Single(Validator.Validate(null, options).Failures!);
        Assert.Contains("Access:RefreshTokens:Purge:LockTimeout is ", failure, StringComparison.Ordinal);
    }

    // Breaks when the StatementTimeout minimum is below 100 ms (99 ms accepted), or the failure does not name the setting;
    // the default LockTimeout is then above it too, so that is refused as well.
    [Fact]
    public void AStatementTimeoutOf99MillisecondsIsRefusedNamingTheStatementTimeout()
    {
        var options = new RefreshTokenPurgeOptions { StatementTimeout = TimeSpan.FromMilliseconds(99) };

        var failures = Validator.Validate(null, options).Failures!.ToList();
        Assert.Contains(failures, f => f.StartsWith("Access:RefreshTokens:Purge:StatementTimeout is ", StringComparison.Ordinal));
    }

    // Breaks when the validator stops at the first failure: an operator must see every bad value at once.
    [Fact]
    public void EveryBadValueIsReportedTogether()
    {
        var options = new RefreshTokenPurgeOptions
        {
            GracePeriod = TimeSpan.Zero,
            BatchSize = 0,
            StatementTimeout = TimeSpan.Zero,
            MaxBatchesPerTenant = 0,
        };

        var failures = Validator.Validate(null, options).Failures!.ToList();

        Assert.Equal(4, failures.Count);
        foreach (var property in new[] { "GracePeriod", "BatchSize", "StatementTimeout", "MaxBatchesPerTenant" })
        {
            Assert.Contains(failures, f => f.Contains(Prefix + property, StringComparison.Ordinal));
        }
    }

    // Breaks when the options are not validated on start, or not bound from Access:RefreshTokens:Purge.
    [Fact]
    public void AHostWithAnOutOfRangePurgeSettingDoesNotStart()
    {
        using var factory = AccessHost.WithoutDatabase().WithSetting(Prefix + "BatchSize", "0");

        StartupFailure.AssertFailsNaming(factory, Prefix + "BatchSize");
    }
}
