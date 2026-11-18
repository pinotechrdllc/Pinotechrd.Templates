using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.BuildingBlocks.Web;

/// <summary>Registers and maps the liveness and readiness probes.</summary>
public static class HealthRegistration
{
    /// <summary>Tag of the health checks that must pass before the application receives traffic.</summary>
    public const string ReadyTag = "ready";

    /// <summary>Path of the liveness probe: it answers while the process runs and runs no checks.</summary>
    public const string LivePath = "/health/live";

    /// <summary>Path of the readiness probe: it runs the checks tagged <see cref="ReadyTag"/>.</summary>
    public const string ReadyPath = "/health/ready";

    /// <summary>Adds the health check services.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The health checks builder, to register checks tagged <see cref="ReadyTag"/>.</returns>
    public static IHealthChecksBuilder AddHealth(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return services.AddHealthChecks();
    }

    /// <summary>
    /// Maps <see cref="LivePath"/> and <see cref="ReadyPath"/>, outside the versioned API group and excluded from the
    /// API description. The readiness probe answers 503 when a check tagged <see cref="ReadyTag"/> is unhealthy.
    /// </summary>
    /// <param name="routes">The application route builder.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapHealth(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        routes.MapHealthChecks(LivePath, new HealthCheckOptions { Predicate = _ => false })
            .ExcludeFromDescription();
        routes.MapHealthChecks(ReadyPath, new HealthCheckOptions { Predicate = c => c.Tags.Contains(ReadyTag) })
            .ExcludeFromDescription();

        return routes;
    }
}
