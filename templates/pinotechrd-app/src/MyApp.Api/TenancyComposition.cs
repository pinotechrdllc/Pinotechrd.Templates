using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api;

/// <summary>
/// Joins the current tenant of the infrastructure building block to the request pipeline of the web building block. It
/// lives here because only the API may set the tenant from a request: the web building block does not see
/// <see cref="ICurrentTenantInitializer"/>, and business code never does.
/// </summary>
public static class TenancyComposition
{
    /// <summary>
    /// Registers the scoped current tenant (<see cref="TenantPersistenceRegistration.AddTenantPersistence"/>) and sets it,
    /// for each authenticated request, from the tenant claim of its token
    /// (<see cref="ApiAuthorizationRegistration.AddTenantFromToken"/>): never from a header, the query string or the body.
    /// An authenticated request without a valid tenant claim is answered 401 on an endpoint that is not anonymous. The one
    /// exception is an anonymous endpoint marked <c>TenantFromHeader()</c> (the login), whose tenant is the active tenant
    /// the <c>X-Tenant</c> header names (<see cref="ApiAuthorizationRegistration.AddTenantFromHeader"/>).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddRequestTenancy(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTenantPersistence();
        services.AddTenantFromToken(SetTenant);
        services.AddTenantFromHeader(SetTenant);
        return services;
    }

    private static void SetTenant(HttpContext context, Guid tenantId) =>
        context.RequestServices.GetRequiredService<ICurrentTenantInitializer>().Initialize(tenantId);
}
