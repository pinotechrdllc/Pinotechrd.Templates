using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using MyApp.BuildingBlocks.Domain;

namespace MyApp.BuildingBlocks.Web.Problems;

/// <summary>Completes every problem details document the API writes with its <c>code</c> and <c>traceId</c>.</summary>
internal static class PipelineProblems
{
    private const string ExceptionKey = "exception";

    /// <summary>
    /// Adds the <c>code</c> when the document has none (validation documents get
    /// <see cref="CommonErrorCodes.ValidationFailed"/>, the others the code of their status) and sets the
    /// <c>traceId</c> to the trace id of the current activity, or to the trace identifier of the request. It
    /// removes the <c>exception</c> member that the developer exception page adds, so exception data never leaves
    /// the server, and names the <c>errors</c> keys like the JSON members (see <see cref="ValidationErrorKeys"/>).
    /// </summary>
    public static void Complete(ProblemDetails problem, HttpContext httpContext)
    {
        problem.Extensions.Remove(ExceptionKey);
        string code;
        if (problem is HttpValidationProblemDetails validation)
        {
            ValidationErrorKeys.Normalize(validation, httpContext);
            code = CommonErrorCodes.ValidationFailed;
        }
        else
        {
            code = CodeForStatus(problem.Status ?? httpContext.Response.StatusCode);
        }

        problem.Extensions.TryAdd(ErrorProblems.CodeKey, code);
        problem.Extensions[ErrorProblems.TraceIdKey] = TraceId(httpContext);
    }

    /// <summary>
    /// Writes a problem details document for <paramref name="status"/> as <c>application/problem+json</c>, through
    /// the problem details service. The <c>code</c> is <paramref name="code"/> when given, otherwise the code
    /// of the status.
    /// </summary>
    public static Task WriteAsync(HttpContext httpContext, int status, string? code = null)
    {
        var problem = new ProblemDetails();
        ErrorProblems.SetStatus(problem, status);
        if (code is not null)
        {
            problem.Extensions[ErrorProblems.CodeKey] = code;
        }

        Complete(problem, httpContext);
        return TypedResults.Problem(problem).ExecuteAsync(httpContext);
    }

    /// <summary>
    /// Returns the code of a status produced without an <see cref="Error"/>. A 500 is a defect
    /// (<see cref="CommonErrorCodes.InternalError"/>); a status with no specific code gets
    /// <see cref="HttpErrorCodes.RequestFailed"/>.
    /// </summary>
    private static string CodeForStatus(int status) => status switch
    {
        StatusCodes.Status400BadRequest => HttpErrorCodes.BadRequest,
        StatusCodes.Status401Unauthorized => CommonErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden => CommonErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => CommonErrorCodes.NotFound,
        StatusCodes.Status405MethodNotAllowed => HttpErrorCodes.MethodNotAllowed,
        StatusCodes.Status408RequestTimeout => HttpErrorCodes.RequestTimeout,
        StatusCodes.Status409Conflict => CommonErrorCodes.Conflict,
        StatusCodes.Status413PayloadTooLarge => HttpErrorCodes.PayloadTooLarge,
        StatusCodes.Status415UnsupportedMediaType => HttpErrorCodes.UnsupportedMediaType,
        StatusCodes.Status500InternalServerError => CommonErrorCodes.InternalError,
        _ => HttpErrorCodes.RequestFailed,
    };

    private static string TraceId(HttpContext httpContext)
    {
        var activity = Activity.Current;
        return activity is not null && activity.TraceId != default
            ? activity.TraceId.ToHexString()
            : httpContext.TraceIdentifier;
    }
}
