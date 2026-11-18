using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using MyApp.BuildingBlocks.Web.Problems;

namespace MyApp.BuildingBlocks.Web.OpenApi;

/// <summary>
/// Describes the problem details schemas as the API writes them: <c>title</c> (a string), <c>status</c> (an integer),
/// <c>code</c> and <c>traceId</c> (see <c>contract/problem-codes.v1.md</c>) are always present, so they are required.
/// </summary>
internal sealed class ProblemDetailsSchemaTransformer : IOpenApiSchemaTransformer
{
    private const string TitleKey = "title";

    private const string StatusKey = "status";

    /// <inheritdoc />
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        if (!typeof(ProblemDetails).IsAssignableFrom(context.JsonTypeInfo.Type))
        {
            return Task.CompletedTask;
        }

        schema.Properties ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);
        schema.Properties[TitleKey] = new OpenApiSchema { Type = JsonSchemaType.String };
        schema.Properties[StatusKey] = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" };
        schema.Properties[ErrorProblems.CodeKey] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "The stable, machine-readable error code, listed in problem-codes.v1.md.",
        };
        schema.Properties[ErrorProblems.TraceIdKey] = new OpenApiSchema
        {
            Type = JsonSchemaType.String,
            Description = "The trace id of the request, to correlate the response with the logs.",
        };
        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
        schema.Required.Add(TitleKey);
        schema.Required.Add(StatusKey);
        schema.Required.Add(ErrorProblems.CodeKey);
        schema.Required.Add(ErrorProblems.TraceIdKey);
        return Task.CompletedTask;
    }
}
