using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace MyApp.BuildingBlocks.Web.Security;

/// <summary>
/// Refuses to start when the platform switch <c>ASPNETCORE_FORWARDEDHEADERS_ENABLED</c> is on and no proxy is
/// configured. The switch (configuration key <see cref="PlatformSwitchKey"/>, compared with <c>true</c> ignoring case,
/// as the platform does) makes the host add its own forwarded headers middleware in front of the whole pipeline and
/// clear the trusted proxy lists; with both lists empty that middleware trusts every sender, so any client could
/// choose its own address and rate limit partition. With proxies configured the lists of this application are applied
/// after the platform's setup, so they win and the switch is harmless.
/// </summary>
internal sealed class ForwardedHeadersSwitchValidator(IConfiguration configuration) : IValidateOptions<HttpForwardedHeadersOptions>
{
    /// <summary>The configuration key the platform reads; the environment variable drops the <c>ASPNETCORE_</c> prefix.</summary>
    public const string PlatformSwitchKey = "ForwardedHeaders_Enabled";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HttpForwardedHeadersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var switchOn = string.Equals("true", configuration[PlatformSwitchKey], StringComparison.OrdinalIgnoreCase);
        return switchOn && !options.IsEnabled
            ? ValidateOptionsResult.Fail(
                $"ASPNETCORE_FORWARDEDHEADERS_ENABLED (configuration key {PlatformSwitchKey}) is true, but "
                + $"{HttpForwardedHeadersOptions.SectionName}:{nameof(HttpForwardedHeadersOptions.KnownProxies)} and "
                + $"{HttpForwardedHeadersOptions.SectionName}:{nameof(HttpForwardedHeadersOptions.KnownNetworks)} are empty, so "
                + "X-Forwarded-For would be trusted from any client. List the reverse proxies in "
                + $"{HttpForwardedHeadersOptions.SectionName}, or unset the variable.")
            : ValidateOptionsResult.Success;
    }
}

/// <summary>
/// Accepts an <c>http</c> origin whose host is not local only in the Development environment: credentials are allowed
/// for every listed origin, and outside development they must not travel unencrypted.
/// </summary>
internal sealed class CorsOriginEnvironmentValidator(IHostEnvironment environment) : IValidateOptions<HttpCorsOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, HttpCorsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        var failures = options.InsecureOrigins()
            .Select(o => HttpCorsOptions.Invalid(
                o,
                $"uses http for a host that is not local in the {environment.EnvironmentName} environment; use https, or list it in appsettings.Development.json only").ErrorMessage!)
            .ToArray();
        return failures.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
