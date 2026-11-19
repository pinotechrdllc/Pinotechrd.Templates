using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.OpenApi;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.BuildingBlocks.Web.OpenApi;

/// <summary>
/// Adds to each operation the problem details responses that the pipeline can return for it: 500 (a defect) always;
/// 400 (a malformed request or a validation failure) when it takes parameters or a body; 408, 413 and 415 (a body
/// sent too slowly, too large, or of another content type) when it takes a body; 429 (rate limited, with a
/// <c>Retry-After</c> header) unless the endpoint is marked with <c>DisableRateLimiting()</c>; 401 when the endpoint
/// requires authentication (authorization metadata, which every endpoint under <c>/api/v1</c> has, without
/// <c>AllowAnonymous()</c>), together with a security requirement on the bearer scheme
/// (<see cref="OpenApiRegistration.BearerSchemeName"/>); 403 when it also requires a permission
/// (<see cref="EndpointAccess.RequirePermission{TBuilder}"/>). An anonymous operation references no scheme. A fallback
/// authorization policy is not endpoint metadata, so it does not add them. Unknown routes and wrong methods are not
/// operations, so they are not listed. An endpoint declares the others it returns (404, 409, 422) with
/// <c>ProducesProblem</c>, and a response it declares is kept. Responses are ordered by status, so the document does
/// not depend on the order in which they were added.
/// </summary>
internal sealed class ProblemResponsesOperationTransformer : IOpenApiOperationTransformer
{
    private const string ProblemJsonContentType = "application/problem+json";

    private const string RetryAfterHeader = "Retry-After";

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

        if (RequiresAuthentication(context))
        {
            await AddAsync(responses, StatusCodes.Status401Unauthorized, typeof(ProblemDetails), context, cancellationToken).ConfigureAwait(false);
            if (RequiresPermission(context))
            {
                await AddAsync(responses, StatusCodes.Status403Forbidden, typeof(ProblemDetails), context, cancellationToken).ConfigureAwait(false);
            }

            operation.Security ??= [];
            operation.Security.Add(new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference(OpenApiRegistration.BearerSchemeName, context.Document)] = [],
            });
        }

        if (IsRateLimited(context))
        {
            await AddAsync(responses, StatusCodes.Status429TooManyRequests, typeof(ProblemDetails), context, cancellationToken).ConfigureAwait(false);
            if (responses[StatusKey(StatusCodes.Status429TooManyRequests)] is OpenApiResponse tooMany && tooMany.Headers is null)
            {
                tooMany.Headers = new Dictionary<string, IOpenApiHeader>(StringComparer.Ordinal)
                {
                    [RetryAfterHeader] = new OpenApiHeader
                    {
                        Description = "The number of seconds to wait before sending another request.",
                        Schema = new OpenApiSchema { Type = JsonSchemaType.Integer, Format = "int32" },
                    },
                };
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

    private static bool RequiresAuthentication(OpenApiOperationTransformerContext context)
    {
        var metadata = context.Description.ActionDescriptor.EndpointMetadata;
        return metadata.Any(m => m is IAuthorizeData or AuthorizationPolicy) && !metadata.OfType<IAllowAnonymous>().Any();
    }

    private static bool RequiresPermission(OpenApiOperationTransformerContext context) =>
        context.Description.ActionDescriptor.EndpointMetadata.OfType<PermissionMetadata>().Any();

    private static bool IsRateLimited(OpenApiOperationTransformerContext context) =>
        !context.Description.ActionDescriptor.EndpointMetadata.OfType<DisableRateLimitingAttribute>().Any();

    private static string StatusKey(int status) => status.ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static async Task AddAsync(
        OpenApiResponses responses,
        int status,
        Type problemType,
        OpenApiOperationTransformerContext context,
        CancellationToken cancellationToken)
    {
        var key = StatusKey(status);
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
