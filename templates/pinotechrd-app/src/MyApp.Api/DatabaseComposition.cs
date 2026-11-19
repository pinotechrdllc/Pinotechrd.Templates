using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.OpenApi;
using MyApp.BuildingBlocks.Web.Telemetry;

namespace MyApp.Api;

/// <summary>
/// Joins the database of the infrastructure building block to the web host: the shared data source, its readiness check
/// and its telemetry. It lives here because only the API knows both the infrastructure and the web building blocks.
/// </summary>
public static class DatabaseComposition
{
    /// <summary>
    /// How long the readiness probe waits for the database before it answers 503. Below the usual probe timeouts of
    /// an orchestrator, so the probe reports the failure instead of timing out itself.
    /// </summary>
    public static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Registers the shared data source (<see cref="DatabaseRegistration.AddDatabase"/>), the
    /// <see cref="DatabaseHealthCheck"/> tagged <see cref="HealthRegistration.ReadyTag"/> with
    /// <see cref="ReadinessTimeout"/>, and the collection of Npgsql's spans and metrics. Nothing connects to the
    /// database until a request or the readiness probe needs it.
    /// <para>
    /// The build-time OpenAPI generation (<c>dotnet build -p:UpdateContract=true</c>) runs the entry point and starts the
    /// host with no server and no configuration (<see cref="ApiDocumentGeneration.IsRunning"/>), so there the connection
    /// string is not validated at start. The services are still registered, so the API description sees the same services
    /// as the running application.
    /// </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDatabaseWithReadiness(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddDatabase(validateOnStart: !ApiDocumentGeneration.IsRunning);
        services.AddHealth()
            .AddCheck<DatabaseHealthCheck>(DatabaseHealthCheck.Name, tags: [HealthRegistration.ReadyTag], timeout: ReadinessTimeout);
        services.AddTelemetrySource(DatabaseRegistration.TelemetryName);
        services.AddTelemetryMeter(DatabaseRegistration.TelemetryName);
        return services;
    }
}
