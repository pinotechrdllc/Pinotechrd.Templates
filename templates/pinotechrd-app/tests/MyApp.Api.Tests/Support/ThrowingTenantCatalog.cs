using MyApp.BuildingBlocks.Application.Tenancy;

namespace MyApp.Api.Tests.Support;

/// <summary>An <see cref="ITenantCatalog"/> whose database is down: every question fails, so a test sees what the host does then.</summary>
internal sealed class ThrowingTenantCatalog : ITenantCatalog
{
    public Task<Guid?> FindActiveTenantIdAsync(string slug, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The tenant catalog is unavailable.");

    public Task<bool> IsActiveAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The tenant catalog is unavailable.");
}
