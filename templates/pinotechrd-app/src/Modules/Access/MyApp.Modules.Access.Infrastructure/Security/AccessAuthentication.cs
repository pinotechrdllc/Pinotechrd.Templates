using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Domain;
using MyApp.Modules.Access.Application.Authentication;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Options;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The <see cref="IAccessAuthentication"/> of the Access module, on the security library's auth manager. The library's
/// failures are free text; each operation maps all of them to one stable code. The password change uses Identity's
/// <see cref="UserManager{TUser}"/> directly, to tell the field of a failure, and runs the password policy
/// (<see cref="AccessPasswordPolicy"/>, <see cref="AccessPasswordValidator"/>) through Identity's validators. Logs carry
/// only the user's and the tenant's identifiers, never an email, a slug or a token.
/// </summary>
/// <param name="authManager">The library's auth manager, which reads the tenant from the scope.</param>
/// <param name="securityManager">The library's security manager, to end the sessions of a user whose password changed.</param>
/// <param name="userManager">Identity's user manager of the tenant's users.</param>
/// <param name="context">The tenant context of the scope, for the atomic failed-attempt count.</param>
/// <param name="refreshTokens">The refresh token store of the scope's tenant.</param>
/// <param name="currentTenant">The tenant of the scope.</param>
/// <param name="security">The library's settings, for the refresh token lifetime.</param>
/// <param name="logger">The logger.</param>
public sealed partial class AccessAuthentication(
    IPinotechrdAuthManager<AccessUser> authManager,
    IPinotechrdSecurityManager<AccessUser, AccessRole> securityManager,
    UserManager<AccessUser> userManager,
    AccessDbContext context,
    IRefreshTokenStore refreshTokens,
    ICurrentTenant currentTenant,
    IOptions<PinotechrdSecurityOptions> security,
    ILogger<AccessAuthentication> logger) : IAccessAuthentication
{
    private const string PasswordMismatchCode = nameof(IdentityErrorDescriber.PasswordMismatch);

    private const string ConcurrencyFailureCode = nameof(IdentityErrorDescriber.ConcurrencyFailure);

    private static readonly Error ConcurrentChange =
        Error.Conflict(CommonErrorCodes.Conflict, "The user was changed at the same time; try again.");

    private static readonly Error InvalidCredentials =
        Error.Unauthorized(AuthErrorCodes.InvalidCredentials, "The email, the password or the tenant is not valid.");

    private static readonly Error InvalidRefreshToken =
        Error.Unauthorized(AuthErrorCodes.InvalidRefreshToken, "The refresh token is not valid; sign in again.");

    private static readonly Error UnknownUser =
        Error.Unauthorized(CommonErrorCodes.Unauthorized, "The user does not exist or is inactive.");

    private static readonly Error WrongCurrentPassword = Error.Validation(
        CommonErrorCodes.ValidationFailed,
        "The current password is not valid.",
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [ChangePasswordFields.CurrentPassword] = ["The current password is not valid."],
        });

    /// <inheritdoc />
    public async Task<Result<AuthSession>> LoginAsync(string email, string password, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Without a tenant the library still checks the password against a dummy hash, so the answer takes as long.
        var tenant = TenantOrNull();
        var result = await authManager.LoginAsync(email ?? string.Empty, password ?? string.Empty, Format(tenant), cancellationToken).ConfigureAwait(false);
        if (result is { Succeeded: true, Token: { } token } && Guid.TryParse(result.UserId, out var userId))
        {
            LogLoginSucceeded(logger, userId, tenant);
            return Session(userId, token);
        }

        // Two-factor accounts end here too: the second step is not offered by the API yet.
        LogLoginFailed(logger, tenant);
        return Result.Failure<AuthSession>(InvalidCredentials);
    }

    /// <inheritdoc />
    public async Task<Result<AuthSession>> RefreshAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrEmpty(refreshToken))
        {
            return Result.Failure<AuthSession>(InvalidRefreshToken);
        }

        // A spent token presented again is logged by the library (event 4204), from what the store returns.
        var result = await authManager.RefreshAsync(refreshToken, cancellationToken).ConfigureAwait(false);
        return result is { Succeeded: true, Token: { } token } && Guid.TryParse(result.UserId, out var userId)
            ? Session(userId, token)
            : Result.Failure<AuthSession>(InvalidRefreshToken);
    }

    /// <inheritdoc />
    public async Task LogoutAsync(string? refreshToken, CancellationToken cancellationToken = default)
    {
        // The store reads the scope's tenant schema: without a tenant there is no session to end.
        var tenant = TenantOrNull();
        if (tenant is null || string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        // The store runs a started revoke to the end; the request's token is not passed, so an abort cannot leave the session alive.
        await refreshTokens.RevokeAsync(refreshToken, CancellationToken.None).ConfigureAwait(false);
        LogLoggedOut(logger, tenant);
    }

    /// <inheritdoc />
    public async Task<Result> ChangePasswordAsync(
        Guid userId, string currentPassword, string newPassword, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var id = userId.ToString("D", CultureInfo.InvariantCulture);
        var user = await userManager.FindByIdAsync(id).ConfigureAwait(false);
        if (user is null || !user.IsActive)
        {
            return Result.Failure(UnknownUser);
        }

        // A locked account answers like a wrong password: the change cannot be used to keep guessing.
        if (await userManager.IsLockedOutAsync(user).ConfigureAwait(false))
        {
            return Result.Failure(WrongCurrentPassword);
        }

        var changed = await userManager.ChangePasswordAsync(user, currentPassword ?? string.Empty, newPassword ?? string.Empty)
            .ConfigureAwait(false);
        if (!changed.Succeeded)
        {
            if (changed.Errors.Any(e => e.Code == PasswordMismatchCode))
            {
                // Counted like a failed sign-in, so a stolen access token cannot guess the password without locking it.
                // Not cancellable: the last cancellation point has passed, and an abort must not skip the count.
                await RecordFailedAttemptAsync(user, CancellationToken.None).ConfigureAwait(false);
                return Result.Failure(WrongCurrentPassword);
            }

            // Another change of the same user won the race: the client retries with the password it now has.
            if (changed.Errors.Any(e => e.Code == ConcurrencyFailureCode))
            {
                return Result.Failure(ConcurrentChange);
            }

            return Result.Failure(PolicyFailure(changed));
        }

        // Every session ends, this one included: the refresh tokens are revoked and the security stamp renewed.
        // The request's token is not passed: the password is already changed, so ending the sessions must not be cancelled.
        var tenant = TenantOrNull();
        if (await securityManager.RevokeUserSessionsAsync(id, CancellationToken.None).ConfigureAwait(false))
        {
            LogPasswordChanged(logger, userId, tenant);
        }
        else
        {
            // The password is changed and Identity renewed the stamp: the request still succeeds, but the revocation is not confirmed.
            LogSessionsNotEnded(logger, userId, tenant);
        }

        return Result.Success();
    }

    // Identity's AccessFailedAsync is read-modify-write on a concurrency stamp: parallel failures lose counts (or fail).
    // One UPDATE increments in the database instead, with Identity's rule: at the limit the account locks for the
    // configured time and the count starts again. PostgreSQL evaluates every SET on the old row, under the row lock.
    // A failure that lands once the account is already locked (it raced the one that locked it) is not counted, so the
    // count of a locked account stays 0 and the next lockout again needs the full number of failures.
    private async Task RecordFailedAttemptAsync(AccessUser user, CancellationToken cancellationToken)
    {
        var lockout = userManager.Options.Lockout;
        var limit = lockout.MaxFailedAccessAttempts;
        var now = DateTimeOffset.UtcNow;
        DateTimeOffset? lockedUntil = now.Add(lockout.DefaultLockoutTimeSpan);
        var stamp = Guid.NewGuid().ToString();
        var id = user.Id;
        await context.Users
            .Where(u => u.Id == id && (u.LockoutEnd == null || u.LockoutEnd <= now))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(u => u.LockoutEnd, u => u.AccessFailedCount + 1 >= limit ? lockedUntil : u.LockoutEnd)
                    .SetProperty(u => u.AccessFailedCount, u => u.AccessFailedCount + 1 >= limit ? 0 : u.AccessFailedCount + 1)
                    .SetProperty(u => u.ConcurrencyStamp, stamp),
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Only the password rules fail a change whose current password matched; anything else is a defect, not the user's input.
    private static Error PolicyFailure(IdentityResult changed)
    {
        if (changed.Errors.Any(e => !e.Code.StartsWith("Password", StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Changing the password failed: " + string.Join(", ", changed.Errors.Select(e => e.Code)) + ".");
        }

        // Identity's length rule and the policy's both report a short password: each message once.
        var messages = changed.Errors.Select(e => e.Description).Distinct(StringComparer.Ordinal).ToArray();
        return Error.Validation(
            CommonErrorCodes.ValidationFailed,
            "The new password does not meet the password policy.",
            new Dictionary<string, string[]>(StringComparer.Ordinal) { [ChangePasswordFields.NewPassword] = messages });
    }

    private Result<AuthSession> Session(Guid userId, Pinotechrd.Security.Models.AuthToken token) =>
        Result.Success(new AuthSession(
            userId,
            token.AccessToken,
            new DateTimeOffset(DateTime.SpecifyKind(token.ExpiresAt, DateTimeKind.Utc)),
            token.RefreshToken,
            TimeSpan.FromDays(security.Value.Jwt.RefreshTokenExpiryDays)));

    private Guid? TenantOrNull() => currentTenant.IsSet ? currentTenant.TenantId : null;

    // The library compares the tenant with the one in its context, written in the same "D" form.
    private static string Format(Guid? tenant) => tenant?.ToString("D", CultureInfo.InvariantCulture) ?? string.Empty;

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in succeeded for user {UserId} in tenant {TenantId}.")]
    private static partial void LogLoginSucceeded(ILogger logger, Guid userId, Guid? tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Sign-in failed in tenant {TenantId}.")]
    private static partial void LogLoginFailed(ILogger logger, Guid? tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Signed out of a session in tenant {TenantId}.")]
    private static partial void LogLoggedOut(ILogger logger, Guid? tenantId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Password changed for user {UserId} in tenant {TenantId}; every session ended.")]
    private static partial void LogPasswordChanged(ILogger logger, Guid userId, Guid? tenantId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Password changed for user {UserId} in tenant {TenantId}, but ending every session was not confirmed.")]
    private static partial void LogSessionsNotEnded(ILogger logger, Guid userId, Guid? tenantId);
}
