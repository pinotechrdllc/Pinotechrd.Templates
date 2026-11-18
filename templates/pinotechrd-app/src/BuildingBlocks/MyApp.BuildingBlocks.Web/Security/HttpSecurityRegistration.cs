using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.BuildingBlocks.Web.Telemetry;

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
    /// language, the bearer token, the JSON content type and the conditional request headers.
    /// </summary>
    public static IReadOnlyList<string> CorsRequestHeaders { get; } =
        ["Accept", "Accept-Language", "Authorization", "Content-Type", "If-Match", "If-None-Match"];

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

    /// <summary>
    /// Builds the front of the pipeline in the one order that is correct, so the host does not have to know it:
    /// <list type="number">
    /// <item><description>Forwarded headers, only when a proxy is configured: everything after it, including the
    /// HSTS decision, the logs and the rate limiter, must see the real client address and scheme.</description></item>
    /// <item><description>The request completion event (<see cref="RequestLogging"/>), only when
    /// <see cref="TelemetryRegistration.AddTelemetry"/> registered its services. It wraps everything after it, so it
    /// times the whole request and sees the final status, the 500 written by the exception handler included: the
    /// exception is logged once, by the handler, and the completion event of that request is at Error. It records no
    /// header, query string or body. Right after routing, a step records the matched route template for it, because the
    /// exception handler clears the endpoint before it writes the 500.</description></item>
    /// <item><description>Security headers, written when the response starts: the error responses that the
    /// exception handler writes after clearing the response get them too, and so do CORS preflight answers.</description></item>
    /// <item><description>CORS, only when an origin is listed: it answers preflight requests itself and writes its
    /// headers when the response starts, so a 500 for an allowed origin keeps them and the browser shows the real
    /// error instead of a CORS failure. It runs before the exception handler, so nothing it does is skipped by an error.</description></item>
    /// <item><description><see cref="ProblemsRegistration.UseProblems"/>: the exception handler, the status code
    /// pages and routing.</description></item>
    /// <item><description>Rate limiting, after routing because it reads the endpoint metadata that exempts the
    /// health probes, and inside the exception handler, so its 429 is written by the problem details pipeline.</description></item>
    /// <item><description>Authentication, then authorization, each only when its services are registered (the same
    /// test <c>WebApplication</c> uses). After routing, because authorization needs the matched endpoint; inside the
    /// exception handler, so their body-less 401 and 403 become problem details (<c>unauthorized</c>,
    /// <c>forbidden</c>) with the security and CORS headers; and after the rate limiter, so a flood of requests is
    /// rejected before any token is validated. Placing them here also stops <c>WebApplication</c> from adding them on
    /// its own in front of this whole pipeline, before routing, where authorization sees no endpoint and every secured
    /// endpoint fails with 500. The global limit is therefore per client address, never per user: a per-user or
    /// per-tenant limit needs the authenticated identity, so it must be a separate limiter placed after
    /// authentication (added here, after authorization), not a policy of this one.</description></item>
    /// </list>
    /// Call it once, instead of <see cref="ProblemsRegistration.UseProblems"/>, before mapping endpoints. The
    /// application does not call <c>UseAuthentication</c> or <c>UseAuthorization</c> itself. Calling them again
    /// afterwards is harmless but redundant: the second authentication reuses the result cached for the request, and
    /// the second authorization evaluates the same policy again.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseApiPipeline(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var services = app.ApplicationServices;
        // Reading the settings here validates them before the first request, as ValidateOnStart does at start.
        var forwarded = services.GetRequiredService<IOptions<HttpForwardedHeadersOptions>>().Value;
        var cors = services.GetRequiredService<IOptions<HttpCorsOptions>>().Value;
        _ = services.GetRequiredService<IOptions<HttpRateLimitOptions>>().Value;
        var hstsEnabled = !services.GetRequiredService<IHostEnvironment>().IsDevelopment();

        // With no known proxy the platform middleware would trust every sender, so it is not added at all.
        if (forwarded.IsEnabled)
        {
            app.UseForwardedHeaders();
        }

        var requestLogging = RequestLogging.IsRegistered(services);
        if (requestLogging)
        {
            RequestLogging.UseCompletionEvent(app);
        }

        var corsEnabled = cors.AllowedOrigins.Count > 0;
        app.Use(next => context =>
        {
            SecurityHeaders.Register(context, hstsEnabled, corsEnabled);
            return next(context);
        });

        if (corsEnabled)
        {
            app.UseCors(CorsPolicyName);
        }

        app.UseProblems();
        if (requestLogging)
        {
            RequestLogging.UseRouteCapture(app);
        }

        app.UseRateLimiter();

        var registered = services.GetService<IServiceProviderIsService>();
        if (registered?.IsService(typeof(IAuthenticationSchemeProvider)) is true)
        {
            app.UseAuthentication();
        }

        if (registered?.IsService(typeof(IAuthorizationHandlerProvider)) is true)
        {
            app.UseAuthorization();
        }

        return app;
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
