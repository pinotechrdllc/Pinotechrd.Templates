using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;

namespace MyApp.BuildingBlocks.Web.OpenApi;

/// <summary>
/// Adds to each operation the problem details responses that the pipeline can return for it: 500 (a defect) always;
/// 400 (a malformed request or a validation failure) when it takes parameters or a body; 408, 413 and 415 (a body
/// sent too slowly, too large, or of another content type) when it takes a body. Unknown routes and wrong methods are
/// not operations, so they are not listed. An endpoint declares the others it returns (404, 409, 422) with
/// <c>ProducesProblem</c>, and a response it declares is kept. Responses are ordered by status, so the document does
/// not depend on the order in which they were added.
/// </summary>
internal sealed class ProblemResponsesOperationTransformer : IOpenApiOperationTransformer
{
    private const string ProblemJsonContentType = "application/problem+json";

    private static readonly int[] BodyStatuses =
    [
        StatusCodes.Status408RequestTimeout,
        StatusCodes.Status413PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType,
    ];

    /// <inheritdoc />
    public async Task TransformAsync(OpenApiOperation operation, OpenApiOperationTransformerContext context, CancellationToken cancellationToken)
    {
        var responses = operation.Responses ??= [];
        var hasBody = operation.RequestBody is not null;
        if (hasBody || operation.Parameters is { Count: > 0 })
        {
            await AddAsync(responses, StatusCodes.Status400BadRequest, typeof(HttpValidationProblemDetails), context, cancellationToken).ConfigureAwait(false);
        }

        if (hasBody)
        {
            foreach (var status in BodyStatuses)
            {
                await AddAsync(responses, status, typeof(ProblemDetails), context, cancellationToken).ConfigureAwait(false);
            }
        }

        await AddAsync(responses, StatusCodes.Status500InternalServerError, typeof(ProblemDetails), context, cancellationToken).ConfigureAwait(false);

        var ordered = responses.OrderBy(r => r.Key, StringComparer.Ordinal).ToArray();
        responses.Clear();
        foreach (var (status, response) in ordered)
        {
            responses.Add(status, response);
        }
    }

    private static async Task AddAsync(
        OpenApiResponses responses,
        int status,
        Type problemType,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var key = status.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (responses.ContainsKey(key))
        {
            return;
        }

        var schemaId = problemType.Name;
        var document = context.Document!;
        var schema = await context.GetOrCreateSchemaAsync(problemType, null, cancellationToken).ConfigureAwait(false);
        document.AddComponent<IOpenApiSchema>(schemaId, schema);
        responses.Add(key, new OpenApiResponse
        {
            Description = ReasonPhrases.GetReasonPhrase(status),
            Content = new Dictionary<string, OpenApiMediaType>(StringComparer.Ordinal)
            {
                [ProblemJsonContentType] = new() { Schema = new OpenApiSchemaReference(schemaId, document) },
            },
        });
    }
}
