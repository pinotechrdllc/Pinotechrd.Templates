using System.Reflection;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Endpoints;

/// <summary>Response of <c>GET /api/v1/version</c>.</summary>
/// <param name="Version">The informational version of the running application.</param>
public sealed record VersionResponse(string Version);

/// <summary>Returns the informational version of the running application.</summary>
public sealed class GetVersionEndpoint : IEndpoint
{
    private static readonly VersionResponse Response = new(ReadVersion());

    /// <inheritdoc />
    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapGet("/version", () => TypedResults.Ok(Response))
            .WithName("GetVersion")
            .WithSummary("Gets the informational version of the running application.");

    private static string ReadVersion()
    {
        // The API assembly is the application: under a test host the entry assembly is the test runner.
        var assembly = ApiAssembly.Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? string.Empty;
    }
}
