using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Web.Authorization;
using Pinotechrd.Security.Tenancy;

namespace MyApp.Modules.Access.Endpoints.Security;

/// <summary>
/// Copies the request's tenant, which the built-in step set from the token, into the security library's scoped
/// <see cref="TenantContext"/>, which its engine and its login compare with the token. It runs at
/// <see cref="AuthenticatedRequestStepOrder.SecurityContext"/>, after the tenant step and before authorization, and does
/// nothing for a request without a tenant. The library's own tenant resolution middleware is not used: it would also
/// accept a tenant from a header for an anonymous request.
/// </summary>
internal sealed class SecurityTenantContextStep : IAuthenticatedRequestStep
{
    public int Order => AuthenticatedRequestStepOrder.SecurityContext;

    public ValueTask<IResult?> InvokeAsync(HttpContext context)
    {
        if (context.RequestServices.GetService<ICurrentTenant>() is { IsSet: true } tenant)
        {
            context.RequestServices.GetRequiredService<TenantContext>().SetTenant(tenant.TenantId.ToString("D"));
        }

        return ValueTask.FromResult<IResult?>(null);
    }
}
