namespace MyApp.BuildingBlocks.Web.Problems;

/// <summary>
/// Error codes that only the HTTP pipeline produces, for requests that never reach a use case. Codes follow the
/// rules of <see cref="Domain.CommonErrorCodes"/> and are listed in <c>contract/problem-codes.v1.md</c>.
/// </summary>
public static class HttpErrorCodes
{
    /// <summary>The request body is not valid JSON or does not match the expected shape (400).</summary>
    public const string InvalidJson = "invalid_json";

    /// <summary>
    /// The request is malformed in any other way: a route, query or header value is missing or cannot be parsed, or
    /// a required body is missing (400).
    /// </summary>
    public const string BadRequest = "bad_request";

    /// <summary>The route exists but does not accept the HTTP method (405).</summary>
    public const string MethodNotAllowed = "method_not_allowed";

    /// <summary>The request body was sent too slowly, below the server's minimum data rate (408).</summary>
    public const string RequestTimeout = "request_timeout";

    /// <summary>The request body is larger than the server accepts (413).</summary>
    public const string PayloadTooLarge = "payload_too_large";

    /// <summary>The request body has a content type the endpoint does not accept (415).</summary>
    public const string UnsupportedMediaType = "unsupported_media_type";

    /// <summary>
    /// Any other error status the pipeline produces without a more specific code. It is a fallback: a status that
    /// gets it today may get a specific code later.
    /// </summary>
    public const string RequestFailed = "request_failed";
}
