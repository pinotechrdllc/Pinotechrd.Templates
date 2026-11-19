using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Web.Authorization;
using Pinotechrd.Security.Tenancy;

namespace MyApp.Modules.Access.Endpoints.Security;

/// <summary>
/// Stops the host at start when <see cref="ApiIdentityOptions.TenantIdClaimType"/> is not the tenant claim the security
/// library writes into its tokens and reads back. The library takes that claim type once, when the module registers it,
/// so a value changed afterwards (by a configuration source added later) would make every token answer 401 or 403.
/// </summary>
/// <param name="tenancy">The tenant settings the library registered.</param>
internal sealed class TenantClaimTypeValidator(TenantResolutionOptions tenancy) : IValidateOptions<ApiIdentityOptions>
{
    public ValidateOptionsResult Validate(string? name, ApiIdentityOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return string.Equals(options.TenantIdClaimType, tenancy.ClaimType, StringComparison.Ordinal)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{ApiIdentityOptions.SectionName}:{nameof(ApiIdentityOptions.TenantIdClaimType)} is '{options.TenantIdClaimType}', "
                + $"but the security library writes and reads the tenant claim '{tenancy.ClaimType}'. They must be the same claim type; "
                + "set the value before the host is built, where the Access module reads it.");
    }
}
