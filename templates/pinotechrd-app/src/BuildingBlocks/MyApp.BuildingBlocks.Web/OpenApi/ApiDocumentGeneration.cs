using System.Reflection;

namespace MyApp.BuildingBlocks.Web.OpenApi;

/// <summary>
/// Tells whether the process is the build-time generation of the API contract
/// (<c>dotnet build src/MyApp.Api -p:UpdateContract=true</c>). That build runs the entry point with no configuration, no
/// secrets and no server, only to read the endpoints; a setting validated at start that needs a secret or a database
/// (a connection string, a signing key) is skipped then, while its services are still registered so the document
/// describes the same API.
/// </summary>
public static class ApiDocumentGeneration
{
    // The tool the build-time generation runs is the entry assembly then; the check Microsoft documents for it.
    private const string GeneratorAssemblyName = "GetDocument.Insider";

    /// <summary>
    /// Gets a value indicating whether the process is the build-time contract generation. It is <see langword="false"/>
    /// in the running application and under a test host.
    /// </summary>
    public static bool IsRunning =>
        string.Equals(Assembly.GetEntryAssembly()?.GetName().Name, GeneratorAssemblyName, StringComparison.Ordinal);
}
