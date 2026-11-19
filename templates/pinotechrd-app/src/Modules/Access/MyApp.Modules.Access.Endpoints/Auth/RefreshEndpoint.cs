using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Authorization;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.BuildingBlocks.Web.Security;
using MyApp.Modules.Access.Application.Authentication;

namespace MyApp.Modules.Access.Endpoints.Auth;

/// <summary>
/// Renews the session of the <see cref="RefreshCookie"/> in the tenant named by <c>X-Tenant</c>: the token is spent, and
/// the answer is a new access token and a new cookie. No body. Every refusal is 401 <c>invalid_refresh_token</c> and
/// drops the cookie; a token used twice ends its whole session.
/// </summary>
public sealed class RefreshEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapPost("/auth/refresh", RefreshAsync)
            .WithName("Refresh")
            .WithSummary("Renews the session of the refresh cookie: a new access token and a new cookie.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AllowAnonymous()
            .TenantFromHeader()
            .RequireRateLimiting(HttpRateLimiting.AuthPolicy)
            .AddEndpointFilter<CsrfFilter>();

    private static async Task<Results<Ok<AccessTokenResponse>, ProblemHttpResult>> RefreshAsync(
        IAccessAuthentication authentication, HttpContext context)
    {
        var result = await authentication.RefreshAsync(RefreshCookie.Read(context.Request), context.RequestAborted).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return TypedResults.Ok(AccessTokenResponse.Issue(result.Value, context.Response));
        }

        RefreshCookie.Clear(context.Response);
        return result.Error.ToProblem();
    }
}
