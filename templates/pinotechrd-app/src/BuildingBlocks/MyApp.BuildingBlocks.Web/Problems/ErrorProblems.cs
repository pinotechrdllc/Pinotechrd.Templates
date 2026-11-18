using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.WebUtilities;
using MyApp.BuildingBlocks.Domain;

namespace MyApp.BuildingBlocks.Web.Problems;

/// <summary>
/// The single place that maps the error model to HTTP: an <see cref="ErrorKind"/> to a status code and an
/// <see cref="Error"/> to an RFC 9457 problem details document.
/// </summary>
public static class ErrorProblems
{
    /// <summary>The problem details member that carries the stable error code.</summary>
    public const string CodeKey = "code";

    /// <summary>The problem details member that carries the trace id of the request.</summary>
    public const string TraceIdKey = "traceId";

    /// <summary>
    /// Returns the HTTP status of an error kind: <see cref="ErrorKind.Validation"/> 400,
    /// <see cref="ErrorKind.Unauthorized"/> 401, <see cref="ErrorKind.Forbidden"/> 403,
    /// <see cref="ErrorKind.NotFound"/> 404, <see cref="ErrorKind.Conflict"/> 409 and
    /// <see cref="ErrorKind.Failure"/> (or any unknown kind) 422 Unprocessable Content: the request was understood
    /// but a business rule rejected it. An error is an expected failure, so it never maps to 500, which is reserved
    /// for defects reported as exceptions.
    /// </summary>
    /// <param name="kind">The error kind.</param>
    /// <returns>The HTTP status code.</returns>
    public static int ToStatusCode(this ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status422UnprocessableEntity,
    };

    /// <summary>
    /// Creates the problem details of an error: <c>status</c> from its kind, <c>title</c> (and, for 422,
    /// <c>type</c>) from the status,
    /// <c>detail</c> from its message and <c>code</c> from its code. A validation error produces a
    /// <see cref="HttpValidationProblemDetails"/> whose <c>errors</c> holds its messages per field.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>The problem details.</returns>
    public static ProblemDetails ToProblemDetails(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var problem = error.Kind == ErrorKind.Validation
            ? new HttpValidationProblemDetails(error.Details.ToDictionary(d => d.Key, d => d.Value.ToArray(), StringComparer.Ordinal))
            : new ProblemDetails();
        SetStatus(problem, error.Kind.ToStatusCode());
        problem.Detail = error.Message;
        problem.Extensions[CodeKey] = error.Code;
        return problem;
    }

    /// <summary>
    /// Returns an error as a problem details response, for an endpoint that answers a failed result:
    /// <c>result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error.ToProblem()</c>.
    /// </summary>
    /// <param name="error">The error.</param>
    /// <returns>The problem details result.</returns>
    public static ProblemHttpResult ToProblem(this Error error) => TypedResults.Problem(error.ToProblemDetails());

    /// <summary>
    /// Sets the status and its title. A 422 gets the RFC 9110 name and section ("Unprocessable Content"), which the
    /// framework still reports with the RFC 4918 name; other statuses get the framework's reason phrase and type.
    /// </summary>
    internal static void SetStatus(ProblemDetails problem, int status)
    {
        problem.Status = status;
        if (status == StatusCodes.Status422UnprocessableEntity)
        {
            problem.Title = UnprocessableContentTitle;
            problem.Type = UnprocessableContentType;
        }
        else
        {
            problem.Title = ReasonPhrases.GetReasonPhrase(status);
        }
    }

    private const string UnprocessableContentTitle = "Unprocessable Content";

    private const string UnprocessableContentType = "https://www.rfc-editor.org/rfc/rfc9110#section-15.5.21";
}
