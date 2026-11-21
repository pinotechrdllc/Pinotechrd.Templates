using System.Security.Claims;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Web.Authorization;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Endpoints.Security;

/// <summary>
/// Adds the user claim the API reads, <see cref="ApiIdentityOptions.UserIdClaimType"/> (<c>sub</c> by default, RFC
/// 7519), to every access token the security library issues, holding the user's id. The library writes the id as
/// <c>id</c>, which the API never reads. The display name stays the library's default (the email).
/// </summary>
/// <param name="identity">The claim types of the API.</param>
internal sealed class AccessIdentityValidator(IOptions<ApiIdentityOptions> identity) : IIdentityValidator<AccessUser>
{
    public Task<IdentityValidationResult> ValidateAsync(AccessUser user, string tenantId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(user);

        Claim[] claims = [new Claim(identity.Value.UserIdClaimType, user.Id.ToString("D"))];
        return Task.FromResult(new IdentityValidationResult(Succeeded: true, AdditionalClaims: claims));
    }
}
