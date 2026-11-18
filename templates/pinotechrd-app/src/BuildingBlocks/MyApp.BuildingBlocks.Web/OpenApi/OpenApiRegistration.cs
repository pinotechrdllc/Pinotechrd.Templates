using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MyApp.BuildingBlocks.Web.OpenApi;

/// <summary>
/// Describes the API as one OpenAPI document, <see cref="DocumentName"/>. The document is frozen in
/// <c>contract/openapi.v1.json</c>, which is the source clients generate from; the endpoint that serves it is only
/// mapped outside Production, for inspection.
/// </summary>
public static class OpenApiRegistration
{
    /// <summary>The name of the API document, the version of the API it describes.</summary>
    public const string DocumentName = "v1";

    // The project name, not the assembly name, so the contract does not expose internal names.
    private const string Title = "MyApp API";

    /// <summary>
    /// Adds the <see cref="DocumentName"/> document, titled with the project name: without <c>servers</c>, so it does
    /// not depend on the address the application listens on, with the members the API always writes on the problem
    /// details schemas, and with the error responses that the pipeline can return for each operation.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddOpenApiDocument(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOpenApi(DocumentName, options =>
        {
            options.AddDocumentTransformer((document, _, _) =>
            {
                document.Info.Title = Title;
                document.Servers = null;
                return Task.CompletedTask;
            });
            options.AddSchemaTransformer<ProblemDetailsSchemaTransformer>();
            options.AddOperationTransformer<ProblemResponsesOperationTransformer>();
        });
        return services;
    }

    /// <summary>
    /// Maps <c>/openapi/v1.json</c> outside Production. Clients use the committed contract file, not this endpoint.
    /// </summary>
    /// <param name="routes">The application route builder.</param>
    /// <returns>The same route builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapOpenApiDocument(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        if (!routes.ServiceProvider.GetRequiredService<IHostEnvironment>().IsProduction())
        {
            routes.MapOpenApi();
        }

        return routes;
    }
}
