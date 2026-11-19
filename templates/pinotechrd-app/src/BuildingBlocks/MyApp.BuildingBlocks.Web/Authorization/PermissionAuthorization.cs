using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>The authorization requirement of an endpoint that requires a permission.</summary>
/// <param name="Permission">The permission.</param>
internal sealed record PermissionRequirement(string Permission) : IAuthorizationRequirement;

/// <summary>
/// Meets a <see cref="PermissionRequirement"/> when the current user is authenticated and the checker of the
/// permission's module (through <see cref="PermissionCheckerRouter"/>) grants the exact permission. Scoped, so it reads
/// the user and the checkers of the request.
/// </summary>
internal sealed class PermissionAuthorizationHandler(ICurrentUser currentUser, PermissionCheckerRouter checker)
    : AuthorizationHandler<PermissionRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!currentUser.IsAuthenticated)
        {
            return;
        }

        var cancellationToken = context.Resource is HttpContext http ? http.RequestAborted : CancellationToken.None;
        if (await checker.HasPermissionAsync(currentUser, requirement.Permission, cancellationToken).ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}
