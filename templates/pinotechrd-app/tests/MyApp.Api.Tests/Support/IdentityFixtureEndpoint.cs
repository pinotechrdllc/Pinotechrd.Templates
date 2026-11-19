using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Support;

/// <summary>What an identity fixture endpoint saw: the current user and tenant of its request, and whether the principal was authenticated.</summary>
/// <param name="PrincipalAuthenticated">Whether <c>HttpContext.User</c> was authenticated.</param>
/// <param name="UserAuthenticated"><c>ICurrentUser.IsAuthenticated</c>.</param>
/// <param name="UserId"><c>ICurrentUser.UserId</c>, when authenticated.</param>
/// <param name="TenantSet"><c>ICurrentTenant.IsSet</c>.</param>
/// <param name="TenantId"><c>ICurrentTenant.TenantId</c>, when set.</param>
public sealed record WhoAmI(bool PrincipalAuthenticated, bool UserAuthenticated, Guid? UserId, bool TenantSet, Guid? TenantId);

/// <summary>
/// Test-only routes under <c>/api/v1/identity-fixtures</c> that report the current user and tenant of the request, so a
/// test sees what the pipeline set. Added to a host with <c>services.AddSingleton&lt;IEndpoint, IdentityFixtureEndpoint&gt;()</c>
/// (see <see cref="IdentityFixtureExtensions"/>): never part of the real host.
/// </summary>
internal sealed class IdentityFixtureEndpoint : IEndpoint
{
    public const string Base = EndpointRegistration.RoutePrefix + "/identity-fixtures";

    /// <summary>Authenticated users only; answers what it saw. GET and POST, so a body can be sent.</summary>
    public const string WhoAmIRoute = Base + "/whoami";

    /// <summary>Anonymous; answers what it saw, for a caller that may or may not have a valid identity.</summary>
    public const string OpenWhoAmIRoute = Base + "/open-whoami";

    /// <summary>Authenticated users only; waits a moment before it reads the tenant, so concurrent requests interleave.</summary>
    public const string SlowTenantRoute = Base + "/slow-tenant";

    public void MapEndpoint(IEndpointRouteBuilder routes)
    {
        var group = routes.MapGroup("/identity-fixtures");
        group.MapMethods("/whoami", ["GET", "POST"], (HttpContext context, ICurrentUser user, ICurrentTenant tenant) => Results.Ok(Describe(context, user, tenant)))
            .AuthenticatedOnly();
        group.MapGet("/open-whoami", (HttpContext context, ICurrentUser user, ICurrentTenant tenant) => Results.Ok(Describe(context, user, tenant)))
            .AllowAnonymous();
        group.MapGet("/slow-tenant", async (ICurrentTenant tenant, int? delay) =>
        {
            await Task.Delay(delay ?? 20);
            await Task.Yield();
            return Results.Ok(tenant.TenantId);
        }).AuthenticatedOnly();
    }

    private static WhoAmI Describe(HttpContext context, ICurrentUser user, ICurrentTenant tenant) =>
        new(
            context.User.Identities.Any(i => i.IsAuthenticated),
            user.IsAuthenticated,
            user.IsAuthenticated ? user.UserId : null,
            tenant.IsSet,
            tenant.IsSet ? tenant.TenantId : null);
}

internal static class IdentityFixtureExtensions
{
    /// <summary>Adds the identity fixture routes to this host only.</summary>
    public static ApiHostFactory WithIdentityFixtures(this ApiHostFactory factory) =>
        factory.WithServices(services => services.AddSingleton<IEndpoint, IdentityFixtureEndpoint>());
}
