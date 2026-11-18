using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace MyApp.Api.Tests.Support;

/// <summary>A host builder with nothing in it, to add modules to without starting a host.</summary>
internal static class ModuleBuilders
{
    /// <summary>A builder with no configuration source, in the given environment, plus the given settings.</summary>
    public static HostApplicationBuilder Create(string environment = "Production", IDictionary<string, string?>? settings = null)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        if (settings is not null)
        {
            builder.Configuration.AddInMemoryCollection(settings);
        }

        return builder;
    }
}
