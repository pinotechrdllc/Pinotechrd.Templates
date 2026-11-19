namespace MyApp.BuildingBlocks.Application.Identity;

/// <summary>
/// The user the current scope works for (the caller of a request), or no user. It is a scoped service, set once per
/// request by the web building block from the authenticated principal, so a user never carries over to another
/// request. Read-only: business code reads the user, it never chooses it. It holds an opaque identifier only, never a
/// name, an email or a token.
/// </summary>
public interface ICurrentUser
{
    /// <summary>
    /// Gets a value indicating whether the scope has an authenticated user with a valid identifier. Outside a request
    /// (a job, the migrator) it is <see langword="false"/>.
    /// </summary>
    bool IsAuthenticated { get; }

    /// <summary>Gets the opaque identifier of the user.</summary>
    /// <exception cref="InvalidOperationException">The scope has no authenticated user.</exception>
    Guid UserId { get; }
}
