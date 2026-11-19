using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Authorization;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.BuildingBlocks.Web.Security;
using MyApp.Modules.Access.Application.Authentication;

namespace MyApp.Modules.Access.Endpoints.Auth;

/// <summary>Request of <c>POST /api/v1/access/auth/change-password</c>.</summary>
/// <param name="CurrentPassword">The password the caller has now.</param>
/// <param name="NewPassword">The new password, checked against the password policy.</param>
public sealed record ChangePasswordRequest([property: Required] string? CurrentPassword, [property: Required] string? NewPassword);

/// <summary>
/// Changes the caller's password: 204, every session of the user ended and the cookie dropped, so the client signs in
/// again with the new password. A wrong current password (counted towards the lockout) answers 400
/// <c>validation_failed</c> on <c>currentPassword</c>, a password the policy refuses on <c>newPassword</c>.
/// </summary>
public sealed class ChangePasswordEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapPost("/auth/change-password", ChangePasswordAsync)
            .WithName("ChangePassword")
            .WithSummary("Changes the caller's password and ends every session of the user.")
            .ProducesProblem(StatusCodes.Status409Conflict)
            .AuthenticatedOnly()
            .RequireRateLimiting(HttpRateLimiting.AuthPolicy);

    private static async Task<Results<NoContent, ProblemHttpResult>> ChangePasswordAsync(
        ChangePasswordRequest request, IAccessAuthentication authentication, ICurrentUser user, HttpContext context)
    {
        var result = await authentication
            .ChangePasswordAsync(user.UserId, request.CurrentPassword!, request.NewPassword!, context.RequestAborted)
            .ConfigureAwait(false);
        if (result.IsFailure)
        {
            return result.Error.ToProblem();
        }

        RefreshCookie.Clear(context.Response);
        return TypedResults.NoContent();
    }
}
