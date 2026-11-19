using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// Keeps 404 and 405 for anonymous callers. The fallback policy (an authenticated user) is evaluated for every request
/// without authorization metadata, which includes an unknown route (no endpoint) and a wrong method (the platform's 405
/// endpoint, which has no route and no metadata); the platform's handling would answer both with 401. This handler
/// lets such a request through only when both hold: the request matched no routed endpoint, and the failing policy is
/// the fallback policy itself. Every other outcome gets the platform's handling.
/// <para>
/// Letting it through opens nothing, by construction: the step <see cref="ApiPipeline.UseApiPipeline"/> places right
/// after authorization answers a request with no endpoint with 404 and runs the 405 endpoint itself, so no middleware
/// added after the pipeline (static files, <c>app.Use</c>, <c>app.Run</c>) ever sees such a request.
/// </para>
/// </summary>
internal sealed class RoutedOnlyAuthorizationResultHandler(IOptions<AuthorizationOptions> options) : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult) =>
        context.GetEndpoint() is not RouteEndpoint && ReferenceEquals(policy, options.Value.FallbackPolicy)
            ? next(context)
            : _default.HandleAsync(next, context, policy, authorizeResult);
}
