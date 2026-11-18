using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyApp.BuildingBlocks.Web.Problems;

/// <summary>
/// Makes the error responses that the pipeline produces, and the failures endpoints return through
/// <see cref="ErrorProblems.ToProblem"/>, RFC 9457 problem details documents (<c>application/problem+json</c>) with a
/// stable <c>code</c> and a <c>traceId</c>: an unmatched route, a request that could not be bound, a validation
/// failure, an unhandled exception or a failed result. A body an endpoint writes itself, such as
/// <c>Results.BadRequest("text")</c>, is not converted, so endpoints return failures through
/// <see cref="ErrorProblems.ToProblem"/> only.
/// </summary>
public static class ProblemsRegistration
{
    /// <summary>
    /// Adds the problem details service, which completes each document with its <c>code</c> and <c>traceId</c>
    /// whatever the request's <c>Accept</c> header, and the exception handler. Requests that cannot be bound throw in
    /// every environment, so they are answered by the same handler instead of an empty 400.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddProblems(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddProblemDetails(options =>
            options.CustomizeProblemDetails = context => PipelineProblems.Complete(context.ProblemDetails, context.HttpContext));
        // After the default writer, so it only answers requests the default writer declines.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IProblemDetailsWriter, FallbackProblemDetailsWriter>());
        services.AddExceptionHandler<ProblemExceptionHandler>();
        services.Configure<RouteHandlerOptions>(options => options.ThrowOnBadRequest = true);
        return services;
    }

    /// <summary>
    /// Adds the exception handler, the status code pages that turn a body-less error status (404, 405, 415 and so
    /// on) into a problem details document, and then routing, so that routing failures (such as an ambiguous match)
    /// are answered too. No other call to <c>UseRouting</c> is needed. The API host does not call it directly:
    /// <see cref="Security.HttpSecurityRegistration.UseApiPipeline"/> calls it after the middleware that must wrap the
    /// error responses (forwarded headers, security headers, CORS), which write nothing that an error clears, and
    /// before rate limiting. Anything else must come after it, so its errors are answered as problem details.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same application builder, for chaining.</returns>
    public static IApplicationBuilder UseProblems(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseExceptionHandler();
        app.UseStatusCodePages(context => PipelineProblems.WriteAsync(context.HttpContext, context.HttpContext.Response.StatusCode));
        app.UseRouting();
        return app;
    }
}
