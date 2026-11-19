using MyApp.BuildingBlocks.Domain;

namespace MyApp.Modules.Access.Application.Authentication;

/// <summary>
/// Sign-in, session refresh, sign-out and password change for the tenant of the scope. Implemented by the Access module's
/// infrastructure, scoped. Every failure is a stable code, never the security library's text, and no failure tells which
/// accounts or tenants exist.
/// </summary>
public interface IAccessAuthentication
{
    /// <summary>Signs a user of the scope's tenant in and starts a session.</summary>
    /// <param name="email">The user's email.</param>
    /// <param name="password">The user's password.</param>
    /// <param name="cancellationToken">Stops the call before it starts.</param>
    /// <returns>
    /// The session, or <see cref="AuthErrorCodes.InvalidCredentials"/> for every failure: no tenant in the scope, unknown
    /// user, wrong password, locked, inactive or two-factor account.
    /// </returns>
    Task<Result<AuthSession>> LoginAsync(string email, string password, CancellationToken cancellationToken = default);

    /// <summary>Rotates a refresh token of the scope's tenant: the token is spent and the session gets a new one.</summary>
    /// <param name="refreshToken">The refresh token, or <see langword="null"/> when the client sent none.</param>
    /// <param name="cancellationToken">Stops the call before it starts.</param>
    /// <returns>The renewed session, or <see cref="AuthErrorCodes.InvalidRefreshToken"/> for every failure.</returns>
    Task<Result<AuthSession>> RefreshAsync(string? refreshToken, CancellationToken cancellationToken = default);

    /// <summary>Ends the session of a refresh token, when the scope has a tenant and the token is its; otherwise nothing.</summary>
    /// <param name="refreshToken">The refresh token, or <see langword="null"/> when the client sent none.</param>
    /// <param name="cancellationToken">Stops the revocation.</param>
    /// <returns>A task that completes when the session is ended.</returns>
    Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken = default);

    /// <summary>
    /// Changes a user's password and, on success, ends every session of the user (all refresh tokens revoked, security
    /// stamp renewed).
    /// </summary>
    /// <param name="userId">The user, the caller of the request.</param>
    /// <param name="currentPassword">The password the user has now.</param>
    /// <param name="newPassword">The new password, checked against the password policy.</param>
    /// <param name="cancellationToken">Stops the call before it starts.</param>
    /// <returns>
    /// Success, <see cref="CommonErrorCodes.ValidationFailed"/> on <see cref="ChangePasswordFields.CurrentPassword"/> (wrong
    /// password, counted towards the lockout, or a locked account) or on <see cref="ChangePasswordFields.NewPassword"/>
    /// (policy), <see cref="CommonErrorCodes.Conflict"/> when a concurrent change of the user won, or
    /// <see cref="CommonErrorCodes.Unauthorized"/> when the user no longer exists or is inactive.
    /// </returns>
    Task<Result> ChangePasswordAsync(Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default);
}

/// <summary>A session just started or renewed.</summary>
/// <param name="UserId">The signed-in user.</param>
/// <param name="AccessToken">The access token (a JWT).</param>
/// <param name="AccessTokenExpiresAt">When the access token expires.</param>
/// <param name="RefreshToken">The refresh token. A secret of the client: never logged, never in a response body.</param>
/// <param name="RefreshTokenLifetime">How long the refresh token lives, the sliding lifetime of a session.</param>
public sealed record AuthSession(
    Guid UserId,
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    TimeSpan RefreshTokenLifetime);

/// <summary>The field names a password change reports its validation messages under.</summary>
public static class ChangePasswordFields
{
    /// <summary>The current password.</summary>
    public const string CurrentPassword = "currentPassword";

    /// <summary>The new password.</summary>
    public const string NewPassword = "newPassword";
}
