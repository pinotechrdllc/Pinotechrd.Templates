namespace MyApp.Modules.Access.Infrastructure.Persistence;

/// <summary>
/// One sign-in session of a user, in the tenant's <c>refresh_token_families</c> table: every refresh token issued by
/// rotating the first one joins it. No tenant column: the tenant's schema holds it, so another tenant never finds it.
/// Written by <c>DatabaseRefreshTokenStore</c> only, with SQL that reads the database clock.
/// </summary>
public sealed class RefreshTokenFamily
{
    /// <summary>Gets or sets the family identifier, the one the security library passes back when it rotates a token.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the user the session belongs to.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets when the family was created (database clock).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets the hard end of the session: no token of the family lives past it, however often it rotates.</summary>
    public DateTime AbsoluteExpiresAt { get; set; }

    /// <summary>Gets or sets when the family was revoked (logout, sign-out, reuse); <see langword="null"/> while it lives.</summary>
    public DateTime? RevokedAt { get; set; }
}
