namespace MyApp.BuildingBlocks.Application.Tenancy;

/// <summary>
/// The tenant the current scope works for (a request, a job, one tenant of a migration run), or no tenant. It is a
/// scoped service: each scope has its own, set once by the infrastructure (its <c>ICurrentTenantInitializer</c>, which
/// business code cannot reach), so a tenant never carries over to another request or to work running in parallel in
/// another scope. Read-only on purpose: business code reads the tenant, it never chooses it.
/// </summary>
public interface ICurrentTenant
{
    /// <summary>Gets a value indicating whether a tenant was set for this scope.</summary>
    bool IsSet { get; }

    /// <summary>Gets the opaque identifier of the tenant.</summary>
    /// <exception cref="InvalidOperationException">No tenant was set for this scope.</exception>
    Guid TenantId { get; }

    /// <summary>
    /// Gets the name of the database schema that holds the tenant's data, derived from <see cref="TenantId"/> by the
    /// infrastructure building block. Never chosen by a user.
    /// </summary>
    /// <exception cref="InvalidOperationException">No tenant was set for this scope.</exception>
    string SchemaName { get; }
}
