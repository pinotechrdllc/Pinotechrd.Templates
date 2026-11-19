using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Tenancy;

namespace MyApp.BuildingBlocks.Web.Authorization;

/// <summary>
/// The scoped <see cref="ICurrentUser"/>: one per request, set once by <see cref="CurrentUserStep"/>. No static or
/// ambient state.
/// </summary>
internal sealed class CurrentUser : ICurrentUser
{
    private Guid? _userId;

    public bool IsAuthenticated => _userId is not null;

    public Guid UserId => _userId ?? throw new InvalidOperationException("The scope has no authenticated user.");

    public void Set(Guid userId)
    {
        if (_userId is { } current && current != userId)
        {
            throw new InvalidOperationException("The user of this scope is already set; a scope is set once.");
        }

        _userId = userId;
    }

    /// <summary>
    /// Takes the user back, so the scope is anonymous: for a principal a later step refuses on an anonymous endpoint.
    /// </summary>
    public void Clear() => _userId = null;
}

/// <summary>
/// Sets <see cref="ICurrentUser"/> from the claim <see cref="ApiIdentityOptions.UserIdClaimType"/> of an authenticated
/// principal. An authenticated principal without exactly one such claim holding a GUID is answered 401 on an endpoint
/// that requires authentication, and treated as anonymous by <see cref="ICurrentUser"/> on an anonymous one.
/// </summary>
internal sealed class CurrentUserStep(IOptions<ApiIdentityOptions> options) : IAuthenticatedRequestStep
{
    public int Order => AuthenticatedRequestStepOrder.CurrentUser;

    public ValueTask<IResult?> InvokeAsync(HttpContext context)
    {
        if (!PrincipalClaims.IsAuthenticated(context.User))
        {
            return ValueTask.FromResult<IResult?>(null);
        }

        if (PrincipalClaims.TryGetGuid(context.User, options.Value.UserIdClaimType, out var userId))
        {
            context.RequestServices.GetRequiredService<CurrentUser>().Set(userId);
            return ValueTask.FromResult<IResult?>(null);
        }

        return ValueTask.FromResult(PrincipalClaims.RejectUnlessAnonymous(context));
    }
}

/// <summary>
/// Sets the tenant of the request from the claim <see cref="ApiIdentityOptions.TenantIdClaimType"/> of the
/// authenticated identities, through the setter the host gives (the web building block cannot set the tenant itself).
/// Only the token decides: a header, the query string or the body are never read. It acts only when
/// <see cref="ICurrentUser"/> is authenticated, so a principal the user step did not accept (on an anonymous endpoint)
/// gets no tenant. Without exactly one valid tenant claim the request is answered 401 on an endpoint that requires
/// authentication, and goes on as anonymous on an anonymous one: the user set by <see cref="CurrentUserStep"/> is
/// cleared, so <see cref="ICurrentUser"/> is not authenticated. A tenant already set for the request (by an
/// earlier step, at <see cref="AuthenticatedRequestStepOrder.TenantSelection"/>) must be the token's: the same one
/// continues, another one is answered 401.
/// </summary>
internal sealed class TenantFromTokenStep(IOptions<ApiIdentityOptions> options, Action<HttpContext, Guid> setTenant)
    : IAuthenticatedRequestStep
{
    public int Order => AuthenticatedRequestStepOrder.Tenant;

    public ValueTask<IResult?> InvokeAsync(HttpContext context)
    {
        if (context.RequestServices.GetService<ICurrentUser>() is not { IsAuthenticated: true })
        {
            return ValueTask.FromResult<IResult?>(null);
        }

        if (!PrincipalClaims.TryGetGuid(context.User, options.Value.TenantIdClaimType, out var tenantId))
        {
            var rejection = PrincipalClaims.RejectUnlessAnonymous(context);
            if (rejection is null)
            {
                // On an anonymous endpoint the request goes on as anonymous: no user without the tenant of the token.
                context.RequestServices.GetService<CurrentUser>()?.Clear();
            }

            return ValueTask.FromResult(rejection);
        }

        if (context.RequestServices.GetService<ICurrentTenant>() is { IsSet: true } current)
        {
            return ValueTask.FromResult<IResult?>(current.TenantId == tenantId ? null : TypedResults.Challenge());
        }

        setTenant(context, tenantId);
        return ValueTask.FromResult<IResult?>(null);
    }
}

/// <summary>Reads identifiers from a principal: only the value of a claim, never logged or returned.</summary>
internal static class PrincipalClaims
{
    // The standard form, xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx; parsing alone would also accept surrounding white space.
    private const int GuidLength = 36;

    /// <summary>Whether any identity of the principal is authenticated, the test authorization itself uses.</summary>
    public static bool IsAuthenticated(ClaimsPrincipal user) => user.Identities.Any(i => i.IsAuthenticated);

    /// <summary>
    /// Reads the one claim of <paramref name="claimType"/> as a GUID in its standard form
    /// (<c>xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx</c>, nothing around it), not empty. Only the claims of authenticated
    /// identities count, and the claim type is compared exactly, case included (<c>TENANT_ID</c> is not
    /// <c>tenant_id</c>). Two claims of the type are ambiguous and fail.
    /// </summary>
    public static bool TryGetGuid(ClaimsPrincipal user, string claimType, out Guid value)
    {
        value = Guid.Empty;
        var claims = user.Identities
            .Where(i => i.IsAuthenticated)
            .SelectMany(i => i.Claims)
            .Where(c => string.Equals(c.Type, claimType, StringComparison.Ordinal))
            .Take(2)
            .ToList();
        return claims is [{ Value: { Length: GuidLength } text }]
            && Guid.TryParseExact(text, "D", out value)
            && value != Guid.Empty;
    }

    /// <summary>A 401 challenge unless the endpoint allows anonymous callers, in which case the request goes on.</summary>
    public static IResult? RejectUnlessAnonymous(HttpContext context) =>
        EndpointAccess.AllowsAnonymous(context.GetEndpoint()) ? null : TypedResults.Challenge();
}
