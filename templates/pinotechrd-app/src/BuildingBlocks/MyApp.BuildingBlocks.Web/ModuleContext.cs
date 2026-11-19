using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MyApp.BuildingBlocks.Web;

/// <summary>
/// What a module receives to register its services (<see cref="IModule.AddServices"/>). A later need of the modules is
/// added here as a new property, so the signature every module implements does not change.
/// </summary>
public sealed class ModuleContext
{
    /// <summary>Creates the context of a module from the host's services, configuration and environment.</summary>
    /// <param name="services">The service collection of the host.</param>
    /// <param name="configuration">The configuration of the host.</param>
    /// <param name="environment">The environment of the host.</param>
    public ModuleContext(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);

        Services = services;
        Configuration = configuration;
        Environment = environment;
        IsGeneratingApiDocument = OpenApi.ApiDocumentGeneration.IsRunning;
    }

    /// <summary>Gets the service collection of the host, where the module registers its services.</summary>
    public IServiceCollection Services { get; }

    /// <summary>
    /// Gets the configuration of the host. Bind options from it when they are first used, not while registering: the
    /// build-time contract generation starts the host with no configuration.
    /// </summary>
    public IConfiguration Configuration { get; }

    /// <summary>Gets the environment of the host.</summary>
    public IHostEnvironment Environment { get; }

    /// <summary>
    /// Gets a value indicating whether the host runs for the build-time contract generation, with no configuration and
    /// no secrets (see <see cref="OpenApi.ApiDocumentGeneration"/>). A module skips there the start-time validation of a
    /// setting that needs a secret, and still registers every service.
    /// </summary>
    public bool IsGeneratingApiDocument { get; }
}
