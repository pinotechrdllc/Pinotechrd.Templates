using System.Reflection;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>
/// The fixed facts about the running application that every log event and every exported signal carries: its name,
/// the environment it runs in and its version.
/// </summary>
/// <param name="Name">The application name, the host's <see cref="IHostEnvironment.ApplicationName"/>.</param>
/// <param name="Environment">The environment name, such as <c>Production</c>.</param>
/// <param name="Version">The informational version of the application assembly.</param>
internal sealed record ApplicationInfo(string Name, string Environment, string Version)
{
    private const string UnknownVersion = "unknown";

    /// <summary>Reads the facts from the host environment.</summary>
    public static ApplicationInfo From(IHostEnvironment environment) =>
        new(environment.ApplicationName, environment.EnvironmentName, VersionOf(FindAssembly(environment.ApplicationName)));

    /// <summary>
    /// Reads the facts before the host exists: the name and version from <paramref name="assembly"/>, the environment
    /// from the variables the host reads (<c>ASPNETCORE_ENVIRONMENT</c> wins over <c>DOTNET_ENVIRONMENT</c>).
    /// </summary>
    public static ApplicationInfo Before(Assembly assembly)
    {
        var environment = System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT");
        if (string.IsNullOrWhiteSpace(environment))
        {
            environment = System.Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        }

        return new(
            assembly.GetName().Name ?? string.Empty,
            string.IsNullOrWhiteSpace(environment) ? Environments.Production : environment,
            VersionOf(assembly));
    }

    /// <summary>Adds the facts to every event of <paramref name="configuration"/>.</summary>
    public LoggerConfiguration Enrich(LoggerConfiguration configuration) =>
        configuration.Enrich.WithProperty(TelemetryRegistration.ApplicationProperty, Name)
            .Enrich.WithProperty(TelemetryRegistration.EnvironmentProperty, Environment)
            .Enrich.WithProperty(TelemetryRegistration.VersionProperty, Version);

    // The application assembly is loaded by the time the host is built: it holds the entry point. Under a test host or
    // a build-time tool the entry assembly is another one, so it is only the fallback.
    private static Assembly? FindAssembly(string name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, name, StringComparison.Ordinal))
        ?? Assembly.GetEntryAssembly();

    private static string VersionOf(Assembly? assembly) =>
        assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? assembly?.GetName().Version?.ToString()
        ?? UnknownVersion;
}
