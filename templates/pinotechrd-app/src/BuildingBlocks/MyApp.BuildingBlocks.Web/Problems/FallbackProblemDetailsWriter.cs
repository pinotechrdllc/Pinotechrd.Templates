using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace MyApp.BuildingBlocks.Web.Problems;

/// <summary>
/// Writes a problem details document when no earlier writer accepts the request, typically because its
/// <c>Accept</c> header excludes JSON. It runs the same customization as the default writer, so the document still
/// has its <c>code</c> and <c>traceId</c>. Registered after the default writer, so it is only the last resort.
/// </summary>
internal sealed class FallbackProblemDetailsWriter(
    IOptions<ProblemDetailsOptions> problemDetailsOptions,
    IOptions<JsonOptions> jsonOptions) : IProblemDetailsWriter
{
    private const string ProblemJsonContentType = "application/problem+json";

    /// <inheritdoc />
    public bool CanWrite(ProblemDetailsContext context) => true;

    /// <inheritdoc />
    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var httpContext = context.HttpContext;
        var problem = context.ProblemDetails;
        problem.Status ??= httpContext.Response.StatusCode;
        problemDetailsOptions.Value.CustomizeProblemDetails?.Invoke(context);

        var typeInfo = jsonOptions.Value.SerializerOptions.GetTypeInfo(problem.GetType());
        return new ValueTask(httpContext.Response.WriteAsJsonAsync(problem, typeInfo, ProblemJsonContentType, httpContext.RequestAborted));
    }
}
