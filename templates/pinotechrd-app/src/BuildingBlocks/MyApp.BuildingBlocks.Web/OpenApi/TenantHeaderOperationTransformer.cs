using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.BuildingBlocks.Web.OpenApi;

/// <summary>
/// Adds the required <see cref="TenantHeader.Name"/> header parameter to each operation whose endpoint is marked with
/// <see cref="TenantHeader.TenantFromHeader{TBuilder}"/>. It runs before <see cref="ProblemResponsesOperationTransformer"/>,
/// so the operation also lists the 400 a missing header answers.
/// </summary>
internal sealed class TenantHeaderOperationTransformer : IOpenApiOperationTransformer
{
    /// <inheritdoc />
    public Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        if (!context.Description.ActionDescriptor.EndpointMetadata.OfType<TenantFromHeaderMetadata>().Any())
        {
            return Task.CompletedTask;
        }

        operation.Parameters ??= [];
        operation.Parameters.Add(new OpenApiParameter
        {
            Name = TenantHeader.Name,
            In = ParameterLocation.Header,
            Required = true,
            Description = "The slug of the tenant the request is for, such as 'acme'.",
            Schema = new OpenApiSchema { Type = JsonSchemaType.String },
        });
        return Task.CompletedTask;
    }
}
