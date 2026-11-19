using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Domain;
using MyApp.BuildingBlocks.Web.Authorization;
using MyApp.BuildingBlocks.Web.Problems;
using MyApp.Modules.Access.Application.Tenants;

namespace MyApp.Modules.Access.Endpoints.Security;

/// <summary>
/// Refuses a token whose tenant is no longer active (provisioning, suspended or cancelled) with 403
/// <c>tenant_unavailable</c>, whatever the endpoint. It runs at <see cref="AuthenticatedRequestStepOrder.TenantStatus"/>,
/// after the tenant from the token and before the security library sees it, and asks <see cref="ITenantCatalog"/> once
/// per authenticated request, without a cache, so a suspension applies from the next request. An anonymous request
/// is not checked: its only tenant is one chosen by the tenant header, which is set only when active.
/// </summary>
internal sealed class TenantStatusStep : IAuthenticatedRequestStep
{
    private static readonly Error Unavailable =
        Error.Forbidden(TenantErrorCodes.Unavailable, "The tenant of the token is not active.");

    public int Order => AuthenticatedRequestStepOrder.TenantStatus;

    public async ValueTask<IResult?> InvokeAsync(HttpContext context)
    {
        var services = context.RequestServices;
        if (services.GetService<ICurrentUser>() is not { IsAuthenticated: true }
            || services.GetService<ICurrentTenant>() is not { IsSet: true } tenant)
        {
            return null;
        }

        var active = await services.GetRequiredService<ITenantCatalog>()
            .IsActiveAsync(tenant.TenantId, context.RequestAborted)
            .ConfigureAwait(false);
        return active ? null : Unavailable.ToProblem();
    }
}
