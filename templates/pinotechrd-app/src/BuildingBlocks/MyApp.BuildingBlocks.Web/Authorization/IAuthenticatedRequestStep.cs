using Microsoft.AspNetCore.Http;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// A step of the request pipeline that runs after authentication and before authorization, in the fixed slot that
/// <see cref="ApiPipeline.UseApiPipeline"/> reserves for it: it sees the authenticated principal (or an anonymous one)
/// and the matched endpoint, and runs before any authorization policy is evaluated. Uses: setting the current user and
/// tenant, a per-user or per-tenant limit, adding the identity to the logs. Steps run for every request that matched an
/// endpoint, anonymous ones included, except the health probes; never for an unknown route or a wrong method.
/// <para>
/// Steps are singletons resolved once, when the pipeline is built, and run in ascending <see cref="Order"/>; two steps
/// with the same order stop the start. Resolve scoped services from <c>context.RequestServices</c>. Register one with
/// <see cref="ApiAuthorizationRegistration.AddAuthenticatedRequestStep{TStep}"/>.
/// </para>
/// </summary>
public interface IAuthenticatedRequestStep
{
    /// <summary>
    /// Gets the position of the step: lower runs first. The built-in steps use the negative values of
    /// <see cref="AuthenticatedRequestStepOrder"/>; application steps use zero or more.
    /// </summary>
    int Order { get; }

    /// <summary>
    /// Runs the step. Return <see langword="null"/> to go on to the next step; return a result to answer the request
    /// with it and stop: no later step, no authorization and no endpoint run. Answer a failure as a problem document,
    /// with <c>error.ToProblem()</c> or a body-less status (which the pipeline turns into one), or with
    /// <c>TypedResults.Challenge()</c> for a 401 that carries the scheme's <c>WWW-Authenticate</c> header.
    /// </summary>
    /// <param name="context">The request.</param>
    /// <returns><see langword="null"/> to continue, or the response.</returns>
    ValueTask<IResult?> InvokeAsync(HttpContext context);
}

/// <summary>The orders of the built-in <see cref="IAuthenticatedRequestStep"/> implementations.</summary>
public static class AuthenticatedRequestStepOrder
{
    /// <summary>Sets <c>ICurrentUser</c> from the user identifier claim, or answers 401 when it is missing or invalid.</summary>
    public const int CurrentUser = -300;

    /// <summary>
    /// Reserved for the step that selects the tenant of a request made before login (the login endpoint), which the
    /// security module adds. No built-in step uses it. It runs after the current user and before <see cref="Tenant"/>,
    /// which accepts a tenant already set only when it is the token's.
    /// </summary>
    public const int TenantSelection = -250;

    /// <summary>
    /// Sets the current tenant from the tenant identifier claim, or answers 401 when it is missing or invalid, or when the
    /// request already has another tenant.
    /// </summary>
    public const int Tenant = -200;

    /// <summary>Adds the user and the tenant identifiers to the request completion event and to the server span.</summary>
    public const int Telemetry = -100;
}
