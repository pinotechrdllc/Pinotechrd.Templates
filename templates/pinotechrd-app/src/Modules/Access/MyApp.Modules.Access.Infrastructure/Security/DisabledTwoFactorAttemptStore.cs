using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The <see cref="ITwoFactorAttemptStore"/> of a template that does not offer two-factor sign-in: counting an attempt
/// throws, so the library, which counts an attempt before it checks a code, never accepts one. It replaces the library's
/// in-memory store, which the host refuses to start with outside Development. To enable two-factor sign-in, register a
/// durable store instead (<c>AddPinotechrdEfTwoFactorAttemptStore</c>, which needs its own migration).
/// </summary>
public sealed class DisabledTwoFactorAttemptStore : ITwoFactorAttemptStore
{
    /// <inheritdoc />
    public Task<int> RegisterAttemptAsync(string challengeId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(
            "Two-factor sign-in is not enabled in this template; register a durable two-factor attempt store (AddPinotechrdEfTwoFactorAttemptStore) to enable it.");

    /// <inheritdoc />
    public Task ExhaustAsync(string challengeId, DateTimeOffset expiresAt, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
