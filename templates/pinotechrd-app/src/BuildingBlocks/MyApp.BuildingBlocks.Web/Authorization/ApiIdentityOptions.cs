using System.ComponentModel.DataAnnotations;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// Where the identity of a request is read from the authenticated principal, bound from <see cref="SectionName"/>:
/// the claim that holds the user's opaque identifier and the claim that holds the tenant's. Both values are GUIDs.
/// Nothing else of the principal (a name, an email, a role) is read here.
/// </summary>
public sealed class ApiIdentityOptions : IValidatableObject
{
    /// <summary>The configuration section the options are bound from.</summary>
    public const string SectionName = "Identity";

    /// <summary>The default claim of the user identifier: <c>sub</c>, the subject of a JWT (RFC 7519).</summary>
    public const string DefaultUserIdClaimType = "sub";

    /// <summary>The default claim of the tenant identifier.</summary>
    public const string DefaultTenantIdClaimType = "tenant_id";

    /// <summary>
    /// The claim type that holds the user's identifier, a GUID. <c>sub</c> by default. A token handler that renames
    /// inbound claims (the platform's JWT handler maps <c>sub</c> to the long name-identifier URI unless its
    /// <c>MapInboundClaims</c> is off) must be configured to keep it, or this setting changed to match.
    /// </summary>
    public string UserIdClaimType { get; set; } = DefaultUserIdClaimType;

    /// <summary>The claim type that holds the tenant's identifier, a GUID. <c>tenant_id</c> by default.</summary>
    public string TenantIdClaimType { get; set; } = DefaultTenantIdClaimType;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (IsBlank(UserIdClaimType))
        {
            yield return new ValidationResult($"{SectionName}:{nameof(UserIdClaimType)} must name a claim type.", [nameof(UserIdClaimType)]);
        }

        if (IsBlank(TenantIdClaimType))
        {
            yield return new ValidationResult($"{SectionName}:{nameof(TenantIdClaimType)} must name a claim type.", [nameof(TenantIdClaimType)]);
        }
    }

    private static bool IsBlank(string? value) => string.IsNullOrWhiteSpace(value) || value.Any(char.IsWhiteSpace);
}
