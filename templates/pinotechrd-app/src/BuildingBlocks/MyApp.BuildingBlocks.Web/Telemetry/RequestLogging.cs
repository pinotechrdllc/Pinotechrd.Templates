using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>
/// The one completion event written for every HTTP request, when its response has been sent:
/// <c>HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed} ms</c>, with the route template in
/// <see cref="RouteProperty"/> when an endpoint matched and the trace and span ids of the request (<c>@tr</c>,
/// <c>@sp</c>), the same trace id as the <c>traceId</c> of a problem response. <c>RequestPath</c> is the path without
/// the query string. No header, query string, body or client address is ever written: they can carry tokens, cookies
/// and personal data.
/// <para>
/// <c>RequestPath</c> is the raw path, kept because it finds the resource in an incident; the same path is in the
/// request scope of every framework event and in the <c>url.path</c> span attribute. Routes therefore carry opaque
/// identifiers only, such as a UUID: never personal data (a national id, an email, a phone number, a name) and never a
/// secret.
/// </para>
/// <list type="bullet">
/// <item><description>Error: the status is 5xx (an unhandled exception is answered with 500, so its request completes
/// at Error; the exception itself is logged once, by the exception handler), or the request failed after its response
/// had started. In that case the event reports the status that was actually sent, carries no exception (the
/// exception handler logs it) and the connection is aborted, so the client sees an incomplete response.</description></item>
/// <item><description>Verbose, off unless the default level is <c>Trace</c>: a health probe that did not fail with an
/// exception or a 500, so an orchestrator polling every few seconds does not flood the logs. An unhealthy readiness
/// probe (503) is not repeated here; the failing check is logged by the health check service.</description></item>
/// <item><description>Information: everything else, 4xx included. A 4xx is the client's outcome (a validation failure,
/// a missing resource, an expired token, a rate limit), the service worked as designed, and scanners produce them in
/// bulk; Warning would turn client behavior into operational noise. Filter or count them by <c>StatusCode</c>.</description></item>
/// </list>
/// </summary>
public static class RequestLogging
{
    /// <summary>The property of the completion event that holds the route template, such as <c>/api/v1/version</c>.</summary>
    public const string RouteProperty = "RequestRoute";

    /// <summary>The message template of the completion event.</summary>
    public const string MessageTemplate = "HTTP {RequestMethod:l} {RequestPath:l} responded {StatusCode} in {Elapsed:0.0000} ms";

    // Marks a request that failed after its response had started.
    private static readonly object FailedAfterStartKey = new();

    /// <summary>Whether the request logging services are registered (<see cref="TelemetryRegistration.AddTelemetry"/>).</summary>
    internal static bool IsRegistered(IServiceProvider services) => services.GetService<IDiagnosticContext>() is not null;

    /// <summary>
    /// Adds the middleware that writes the completion event once the inner pipeline has finished, and, right inside it,
    /// the step that ends a request that failed after its response had started.
    /// </summary>
    internal static void UseCompletionEvent(IApplicationBuilder app)
    {
        var logger = app.ApplicationServices.GetRequiredService<ILogger>();
        app.UseSerilogRequestLogging(options =>
        {
            options.Logger = logger;
            // The same properties as the default template; ":l" renders the strings without quotes in the message.
            options.MessageTemplate = MessageTemplate;
            options.IncludeQueryInRequestPath = false;
            options.GetLevel = LevelOf;
        });
        app.Use(AbortWhenFailedAfterStart);
    }

    /// <summary>
    /// Adds a middleware, right after routing, that records the route template of the matched endpoint for the
    /// completion event. It is read there and not when the request completes because the exception handler clears the
    /// endpoint before it writes the 500.
    /// </summary>
    internal static void UseRouteCapture(IApplicationBuilder app)
    {
        var diagnosticContext = app.ApplicationServices.GetRequiredService<IDiagnosticContext>();
        app.Use(next => context =>
        {
            if (context.GetEndpoint() is RouteEndpoint { RoutePattern.RawText: { } route })
            {
                diagnosticContext.Set(RouteProperty, route);
            }

            return next(context);
        });
    }

    internal static LogEventLevel LevelOf(HttpContext context, double elapsedMilliseconds, Exception? exception)
    {
        var status = context.Response.StatusCode;
        if (exception is not null || status == StatusCodes.Status500InternalServerError || context.Items.ContainsKey(FailedAfterStartKey))
        {
            return LogEventLevel.Error;
        }

        if (HealthProbes.IsProbe(context.Request.Path))
        {
            return LogEventLevel.Verbose;
        }

        return status >= StatusCodes.Status500InternalServerError ? LogEventLevel.Error : LogEventLevel.Information;
    }

    // Once the response has started the exception handler can only log the exception (it does, once, at Error) and
    // throw it again. Left to escape, it would reach the completion middleware, which would report 500 with the
    // exception, and then the server, which would log it a third time before aborting the connection. Aborting here
    // does what the server would do, and the completion event reports the status that was sent.
    private static async Task AbortWhenFailedAfterStart(HttpContext context, RequestDelegate next)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any exception: the response cannot be completed, whatever the cause.
        catch (Exception) when (context.Response.HasStarted)
#pragma warning restore CA1031
        {
            context.Items[FailedAfterStartKey] = true;
            context.Abort();
        }
    }
}
