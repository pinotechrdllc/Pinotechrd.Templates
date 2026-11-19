using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Authorization;
using MyApp.BuildingBlocks.Web.Security;
using MyApp.Modules.Access.Application.Authentication;

namespace MyApp.Modules.Access.Endpoints.Auth;

/// <summary>
/// Ends the session of the <see cref="RefreshCookie"/> in the tenant named by <c>X-Tenant</c> and drops the cookie. No
/// body. Always 204, with or without a valid cookie, so a client can always sign out. Access tokens already issued stay
/// valid until they expire.
/// </summary>
public sealed class LogoutEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapPost("/auth/logout", LogoutAsync)
            .WithName("Logout")
            .WithSummary("Ends the session of the refresh cookie and drops the cookie.")
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AllowAnonymous()
            .TenantFromHeader()
            .RequireRateLimiting(HttpRateLimiting.AuthPolicy)
            .AddEndpointFilter<CsrfFilter>();

    private static async Task<NoContent> LogoutAsync(IAccessAuthentication authentication, HttpContext context)
    {
        await authentication.LogoutAsync(RefreshCookie.Read(context.Request), context.RequestAborted).ConfigureAwait(false);
        RefreshCookie.Clear(context.Response);
        return TypedResults.NoContent();
    }
}
