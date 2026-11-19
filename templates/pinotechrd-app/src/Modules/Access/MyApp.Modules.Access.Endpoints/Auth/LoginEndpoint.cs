using System.ComponentModel.DataAnnotations;
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

/// <summary>Request of <c>POST /api/v1/access/auth/login</c>.</summary>
/// <param name="Email">The user's email.</param>
/// <param name="Password">The user's password.</param>
public sealed record LoginRequest([property: Required] string? Email, [property: Required] string? Password);

/// <summary>
/// Signs a user of the tenant named by <c>X-Tenant</c> in: 200 with the access token, and the refresh token in the
/// <see cref="RefreshCookie"/>. Every refusal is the same 401 <c>invalid_credentials</c>, without a cookie.
/// </summary>
public sealed class LoginEndpoint : IEndpoint
{
    /// <inheritdoc />
    public void MapEndpoint(IEndpointRouteBuilder routes) =>
        routes.MapPost("/auth/login", LoginAsync)
            .WithName("Login")
            .WithSummary("Signs a user of the tenant named by the X-Tenant header in.")
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .AllowAnonymous()
            .TenantFromHeader()
            .RequireRateLimiting(HttpRateLimiting.AuthPolicy)
            .AddEndpointFilter<CsrfFilter>();

    private static async Task<Results<Ok<AccessTokenResponse>, ProblemHttpResult>> LoginAsync(
        LoginRequest request, IAccessAuthentication authentication, HttpContext context)
    {
        var result = await authentication.LoginAsync(request.Email!, request.Password!, context.RequestAborted).ConfigureAwait(false);
        return result.IsSuccess
            ? TypedResults.Ok(AccessTokenResponse.Issue(result.Value, context.Response))
            : result.Error.ToProblem();
    }
}
