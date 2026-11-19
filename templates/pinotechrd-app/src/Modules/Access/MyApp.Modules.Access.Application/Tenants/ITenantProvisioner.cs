using MyApp.BuildingBlocks.Domain;

namespace MyApp.Modules.Access.Application.Tenants;

/// <summary>
/// Provisions a tenant: records it in the catalog as provisioning, creates and migrates its schema, then marks it active.
/// Idempotent by slug: the same request again reports the tenant already provisioned, or resumes one left provisioning by
/// a failure or a crash. Safe under concurrency: concurrent requests for one slug end with one tenant, each migration
/// applied once. Implemented by the Access module's infrastructure, scoped.
/// </summary>
public interface ITenantProvisioner
{
    /// <summary>Provisions the tenant of <paramref name="request"/>, or completes it.</summary>
    /// <param name="request">The slug, name and subscription; checked before any I/O.</param>
    /// <param name="cancellationToken">Stops the run; what finished stays, and a new request resumes the rest.</param>
    /// <returns>
    /// The tenant, or an expected failure: <see cref="CommonErrorCodes.ValidationFailed"/> (the message never quotes the
    /// rejected value), <see cref="TenantErrorCodes.SlugTaken"/>, <see cref="TenantErrorCodes.SubscriptionNotFound"/>,
    /// <see cref="TenantErrorCodes.Suspended"/>, <see cref="TenantErrorCodes.Cancelled"/> or
    /// <see cref="TenantErrorCodes.ProvisioningFailed"/>.
    /// </returns>
    Task<Result<ProvisionedTenant>> ProvisionAsync(ProvisionTenantRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A provisioned tenant.</summary>
/// <param name="TenantId">The tenant's id.</param>
/// <param name="Slug">The tenant's slug.</param>
/// <param name="AlreadyProvisioned">Whether the tenant was already active before this request.</param>
public sealed record ProvisionedTenant(Guid TenantId, string Slug, bool AlreadyProvisioned);
