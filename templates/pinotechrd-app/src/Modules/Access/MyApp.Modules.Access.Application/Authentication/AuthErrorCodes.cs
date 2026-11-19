namespace MyApp.Modules.Access.Application.Authentication;

/// <summary>
/// Error codes of sign-in and sessions. Like every <c>*ErrorCodes</c> class they are snake_case, unique and only ever
/// added, and listed in <c>contract/problem-codes.v1.md</c>.
/// </summary>
public static class AuthErrorCodes
{
    /// <summary>
    /// The sign-in was refused (401). One answer for every cause (unknown tenant or user, wrong password, locked or
    /// inactive account), so the answer never tells which accounts exist.
    /// </summary>
    public const string InvalidCredentials = "invalid_credentials";

    /// <summary>The refresh token is missing, unknown, expired, revoked or already used (401); the client signs in again.</summary>
    public const string InvalidRefreshToken = "invalid_refresh_token";

    /// <summary>A browser request from another site, or from an origin that is not allowed, was refused (403).</summary>
    public const string CsrfRejected = "csrf_rejected";
}
