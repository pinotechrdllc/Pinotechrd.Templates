namespace MyApp.BuildingBlocks.Domain;

/// <summary>
/// Error codes any layer can return through an <see cref="Error"/>. A code is a public contract: clients key on it,
/// so codes are snake_case, unique across every <c>*ErrorCodes</c> class, and only ever added, never renamed. Every code
/// is listed in <c>contract/problem-codes.v1.md</c>.
/// </summary>
public static class CommonErrorCodes
{
    /// <summary>The input is invalid; the response lists the messages per field.</summary>
    public const string ValidationFailed = "validation_failed";

    /// <summary>The requested resource, or route, does not exist.</summary>
    public const string NotFound = "not_found";

    /// <summary>The request conflicts with the current state, such as a duplicate or a stale version.</summary>
    public const string Conflict = "conflict";

    /// <summary>The caller is not authenticated, or the credentials are not valid.</summary>
    public const string Unauthorized = "unauthorized";

    /// <summary>The caller is authenticated but not allowed to perform the operation.</summary>
    public const string Forbidden = "forbidden";

    /// <summary>The server failed unexpectedly (a defect, never a returned error). No detail of the cause is returned.</summary>
    public const string InternalError = "internal_error";
}
