using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.BuildingBlocks.Web.Security;

/// <summary>
/// The cross-cutting HTTP protections of the API: forwarded headers from trusted proxies, security headers, CORS and
/// a global rate limit. Their settings are bound from the <c>Http</c> configuration section
/// (<see cref="HttpForwardedHeadersOptions"/>, <see cref="HttpCorsOptions"/>, <see cref="HttpRateLimitOptions"/>)
/// and validated when the host starts.
/// </summary>
public static class HttpSecurityRegistration
{
    /// <summary>The name of the CORS policy built from <see cref="HttpCorsOptions"/>.</summary>
    public const string CorsPolicyName = "AllowedOrigins";

    /// <summary>
    /// The methods a listed origin may use: those of a JSON API. <c>HEAD</c> and <c>OPTIONS</c> need no listing.
    /// </summary>
    public static IReadOnlyList<string> CorsMethods { get; } = ["GET", "POST", "PUT", "PATCH", "DELETE"];

    /// <summary>
    /// The request headers a listed origin may send besides the always-allowed ones: content negotiation and
    /// language, the bearer token, the JSON content type, the conditional request headers and the tenant chosen before
    /// login (<see cref="TenantHeader.Name"/>).
    /// </summary>
    public static IReadOnlyList<string> CorsRequestHeaders { get; } =
        ["Accept", "Accept-Language", "Authorization", "Content-Type", "If-Match", "If-None-Match", TenantHeader.Name];

    /// <summary>
    /// The response headers a browser client may read besides the always-exposed ones: when to retry after a 429
    /// (<c>Retry-After</c>), the version of a resource (<c>ETag</c>) and the address of a created one (<c>Location</c>).
    /// </summary>
    public static IReadOnlyList<string> CorsExposedHeaders { get; } = ["Retry-After", "ETag", "Location"];

    /// <summary>How long a browser may cache a preflight answer, so a client does not send one before every call.</summary>
    public static TimeSpan CorsPreflightMaxAge { get; } = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Binds and validates the settings (an invalid one stops the host at start with an
    /// <see cref="OptionsValidationException"/> that names it), and adds the services of forwarded headers, CORS and
    /// rate limiting. It also stops Kestrel from sending the <c>Server</c> header, which only advertises the product.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddHttpSecurity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        AddValidated<HttpForwardedHeadersOptions>(services, HttpForwardedHeadersOptions.SectionName);
        AddValidated<HttpCorsOptions>(services, HttpCorsOptions.SectionName);
        AddValidated<HttpRateLimitOptions>(services, HttpRateLimitOptions.SectionName);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HttpForwardedHeadersOptions>, ForwardedHeadersSwitchValidator>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<HttpCorsOptions>, CorsOriginEnvironmentValidator>());

        // A post-configuration runs after every configuration, including the platform's own setup for
        // ASPNETCORE_FORWARDEDHEADERS_ENABLED, which clears the trusted lists: the lists of this application always win.
        services.AddOptions<ForwardedHeadersOptions>()
            .PostConfigure<IOptions<HttpForwardedHeadersOptions>>((options, settings) => ConfigureForwardedHeaders(options, settings.Value));

        services.AddCors();
        services.AddOptions<CorsOptions>()
            .Configure<IOptions<HttpCorsOptions>>((options, settings) => options.AddPolicy(CorsPolicyName, policy => policy
                .WithOrigins([.. settings.Value.AllowedOrigins])
                .WithMethods([.. CorsMethods])
                .WithHeaders([.. CorsRequestHeaders])
                .WithExposedHeaders([.. CorsExposedHeaders])
                .AllowCredentials()
                .SetPreflightMaxAge(CorsPreflightMaxAge)));

        HttpRateLimiting.AddGlobalLimit(services);

        services.Configure<KestrelServerOptions>(options => options.AddServerHeader = false);
        return services;
    }

    private static void AddValidated<TOptions>(IServiceCollection services, string sectionName)
        where TOptions : class =>
        services.AddOptions<TOptions>()
            .BindConfiguration(sectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

    private static void ConfigureForwardedHeaders(ForwardedHeadersOptions options, HttpForwardedHeadersOptions settings)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        // The defaults trust loopback; only the configured proxies are trusted. With none configured the middleware is
        // not added (and the platform switch is refused at start), so the platform's default limit of one hop is kept.
        if (!settings.IsEnabled)
        {
            return;
        }

        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var proxy in settings.KnownProxies)
        {
            if (HttpForwardedHeadersOptions.TryParseAddress(proxy, out var address))
            {
                options.KnownProxies.Add(address);
            }
        }

        foreach (var network in settings.KnownNetworks)
        {
            if (HttpForwardedHeadersOptions.TryParseNetwork(network, out var parsed))
            {
                options.KnownIPNetworks.Add(parsed);
            }
        }

        // Every hop is checked against the trusted list, so the client address is the first untrusted one from the
        // right, however many trusted proxies the request went through.
        options.ForwardLimit = null;
    }
}
