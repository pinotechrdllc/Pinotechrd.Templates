using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Web.Authorization;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.BuildingBlocks.Web.Security;
using MyApp.BuildingBlocks.Web.Telemetry;

namespace MyApp.BuildingBlocks.Web;

/// <summary>The request pipeline of the API, in the one order that is correct, so the host does not have to know it.</summary>
public static class ApiPipeline
{
    /// <summary>
    /// Builds the front of the pipeline, in this order:
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
    /// health probes, and inside the exception handler, so its 429 is written by the problem details pipeline. It runs
    /// before authentication, so a flood of requests is rejected before any token is validated; the global limit is
    /// therefore per client address, never per user.</description></item>
    /// <item><description>Authentication. After routing, because the steps and authorization need the matched endpoint;
    /// inside the exception handler, so a body-less 401 and 403 become problem details (<c>unauthorized</c>,
    /// <c>forbidden</c>) with the security and CORS headers. Placing it here also stops <c>WebApplication</c> from
    /// adding it, and authorization, on its own in front of this whole pipeline, before routing, where authorization
    /// sees no endpoint and every secured endpoint fails with 500.</description></item>
    /// <item><description>The authenticated-request steps (<see cref="IAuthenticatedRequestStep"/>), in ascending
    /// order, for every request that matched an endpoint other than a health probe: the current user, the tenant from
    /// the token, the identity in the telemetry, then the application's own steps (a per-user or per-tenant limit goes
    /// here). A step that answers stops the request: no later step, no authorization, no endpoint.</description></item>
    /// <item><description>Authorization: the endpoint's policy, or the fallback policy (an authenticated user) for a
    /// routed endpoint without one.</description></item>
    /// <item><description>The end of the pipeline for any request that matched no routed endpoint: no endpoint answers
    /// 404 <c>not_found</c>, and the platform's 405 endpoint (a wrong method) runs here and answers 405. Only routed
    /// endpoints, which authorization has just checked, go further, so nothing added after this call (static files,
    /// <c>app.Use</c>, <c>app.Run</c>) can answer a request authorization let through. Middleware that must serve
    /// requests without an endpoint, such as static files, goes before the pipeline and is public.</description></item>
    /// </list>
    /// Call it once, before mapping endpoints, after <see cref="ApiAuthorizationRegistration.AddApiAuthorization"/>.
    /// The application does not call <c>UseRouting</c>, <c>UseAuthentication</c> or <c>UseAuthorization</c> itself.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// <see cref="ApiAuthorizationRegistration.AddApiAuthorization"/> was not called, or two authenticated-request
    /// steps have the same order.
    /// </exception>
    public static IApplicationBuilder UseApiPipeline(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var services = app.ApplicationServices;
        if (!ApiAuthorizationRegistration.IsRegistered(services))
        {
            throw new InvalidOperationException(
                "The API pipeline denies by default and needs its authorization services: call services.AddApiAuthorization() when registering services.");
        }

        // Reading the settings here validates them before the first request, as ValidateOnStart does at start.
        var forwarded = services.GetRequiredService<IOptions<HttpForwardedHeadersOptions>>().Value;
        var cors = services.GetRequiredService<IOptions<HttpCorsOptions>>().Value;
        _ = services.GetRequiredService<IOptions<HttpRateLimitOptions>>().Value;
        var hstsEnabled = !services.GetRequiredService<IHostEnvironment>().IsDevelopment();
        var steps = OrderedSteps(services);

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
            app.UseCors(HttpSecurityRegistration.CorsPolicyName);
        }

        app.UseProblems();
        if (requestLogging)
        {
            RequestLogging.UseRouteCapture(app);
        }

        app.UseRateLimiter();
        app.UseAuthentication();
        if (steps.Length > 0)
        {
            app.Use(next => context => RunStepsAsync(steps, next, context));
        }

        app.UseAuthorization();
        app.Use(next => context => EndOfPipelineAsync(next, context));
        return app;
    }

    // Requests without a routed endpoint end here (see the list above); routed endpoints go on to be executed.
    private static Task EndOfPipelineAsync(RequestDelegate next, HttpContext context)
    {
        switch (context.GetEndpoint())
        {
            case RouteEndpoint:
                return next(context);
            case { RequestDelegate: { } rejection }:
                return rejection(context);
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
        }
    }

    private static IAuthenticatedRequestStep[] OrderedSteps(IServiceProvider services)
    {
        var steps = services.GetServices<IAuthenticatedRequestStep>().OrderBy(s => s.Order).ToArray();
        var clashes = steps.GroupBy(s => s.Order).Where(g => g.Count() > 1)
            .Select(g => $"{string.Join(" and ", g.Select(s => s.GetType().FullName))} (order {g.Key})")
            .ToList();
        if (clashes.Count > 0)
        {
            throw new InvalidOperationException(
                "Authenticated-request steps must each have their own order: " + string.Join("; ", clashes) + ".");
        }

        return steps;
    }

    // Only requests that matched an endpoint: an unknown route or a wrong method has nothing to authorize, and the
    // health probes must answer whatever the identity of the caller.
    private static async Task RunStepsAsync(IAuthenticatedRequestStep[] steps, RequestDelegate next, HttpContext context)
    {
        if (context.GetEndpoint() is RouteEndpoint && !HealthProbes.IsProbe(context.Request.Path))
        {
            foreach (var step in steps)
            {
                if (await step.InvokeAsync(context).ConfigureAwait(false) is { } answer)
                {
                    await answer.ExecuteAsync(context).ConfigureAwait(false);
                    return;
                }
            }
        }

        await next(context).ConfigureAwait(false);
    }
}
