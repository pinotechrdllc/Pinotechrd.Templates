namespace MyApp.Modules.Access.Infrastructure.Persistence;

/// <summary>
/// One refresh token, in the tenant's <c>refresh_tokens</c> table: only the SHA-256 of its value is stored, never the
/// value. No tenant column: the tenant's schema holds it. Written by <c>DatabaseRefreshTokenStore</c> only, with SQL
/// that reads the database clock.
/// </summary>
public sealed class RefreshToken
{
    /// <summary>The length of <see cref="TokenHash"/>, a SHA-256.</summary>
    public const int HashLength = 32;

    /// <summary>Gets or sets the row identifier.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the family (sign-in session) the token belongs to.</summary>
    public Guid FamilyId { get; set; }

    /// <summary>Gets or sets the SHA-256 of the token's UTF-8 value, unique.</summary>
    public byte[] TokenHash { get; set; } = [];

    /// <summary>Gets or sets the user the token was issued to.</summary>
    public Guid UserId { get; set; }

    /// <summary>Gets or sets the user's security stamp when the token was issued, if the user store has one.</summary>
    public string? SecurityStamp { get; set; }

    /// <summary>Gets or sets when the token was issued (database clock).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>Gets or sets when the token expires: the sliding expiry, capped by the family's absolute expiry.</summary>
    public DateTime ExpiresAt { get; set; }

    /// <summary>Gets or sets when the token was consumed by a refresh; <see langword="null"/> while unused.</summary>
    public DateTime? ConsumedAt { get; set; }

    /// <summary>Gets or sets when the token was revoked on its own; <see langword="null"/> otherwise.</summary>
    public DateTime? RevokedAt { get; set; }
}
