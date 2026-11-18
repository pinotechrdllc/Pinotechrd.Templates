using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using MyApp.BuildingBlocks.Domain;

namespace MyApp.BuildingBlocks.Web.Problems;

/// <summary>
/// Answers every exception that reaches the pipeline with a problem details document and logs it. A request that
/// could not be bound (<see cref="BadHttpRequestException"/>) keeps its status, usually 400, with
/// <see cref="HttpErrorCodes.InvalidJson"/> when the body could not be read as JSON and the code of its status
/// otherwise; any other exception is a defect and answers 500 <see cref="CommonErrorCodes.InternalError"/>. The body
/// never carries the exception message, type or stack trace, in any environment.
/// </summary>
internal sealed partial class ProblemExceptionHandler(ILogger<ProblemExceptionHandler> logger) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        int status;
        string? code = null;
        if (exception is BadHttpRequestException badRequest)
        {
            status = badRequest.StatusCode;
            if (badRequest.InnerException is JsonException)
            {
                code = HttpErrorCodes.InvalidJson;
            }

            // Without the exception: its message quotes the value that could not be bound, which may come from the
            // query string or a header and carry a token. The request's completion event records the status.
            LogBadRequest(logger, status);
        }
        else
        {
            status = StatusCodes.Status500InternalServerError;
            code = CommonErrorCodes.InternalError;
            LogUnhandled(logger, exception);
        }

        await PipelineProblems.WriteAsync(httpContext, status, code).ConfigureAwait(false);
        return true;
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "The request could not be bound and is answered with status {Status}.")]
    private static partial void LogBadRequest(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Error, Message = "An unhandled exception occurred while processing the request.")]
    private static partial void LogUnhandled(ILogger logger, Exception exception);
}
