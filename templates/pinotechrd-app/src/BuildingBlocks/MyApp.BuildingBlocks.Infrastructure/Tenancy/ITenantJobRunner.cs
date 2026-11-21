namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Runs background work once for each tenant in service. Business code supplies the work and never the tenant: the tenants
/// come from <see cref="Migrations.ITenantDirectory.ListInServiceTenantIdsAsync"/>, are visited one at a time, and each
/// visit runs in a new async scope whose tenant is set with <see cref="ICurrentTenantInitializer.Initialize"/> (never the
/// migration variant), so the work reads <see cref="Application.Tenancy.ICurrentTenant"/> and the tenant's contexts as a
/// request would.
/// <para>
/// A failure of the work does not stop the others: it is returned in <see cref="TenantJobOutcome{T}.Error"/>. Only an
/// <see cref="OperationCanceledException"/> raised while the token is cancelled is rethrown. A failure of the directory is
/// thrown. With no <see cref="Migrations.ITenantDirectory"/> registered no tenant is visited.
/// </para>
/// </summary>
public interface ITenantJobRunner
{
    /// <summary>Runs <paramref name="work"/> once for every in-service tenant, one after another.</summary>
    /// <typeparam name="T">What the work returns.</typeparam>
    /// <param name="work">The work, given the provider of the tenant's scope and the token.</param>
    /// <param name="cancellationToken">Stops the run.</param>
    /// <returns>One outcome per tenant visited, in the directory's order.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    Task<IReadOnlyList<TenantJobOutcome<T>>> ForEachTenantAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken);
}

/// <summary>The result of the work for one tenant.</summary>
/// <typeparam name="T">What the work returns.</typeparam>
/// <param name="TenantId">The opaque identifier of the tenant; fine to log.</param>
/// <param name="Result">What the work returned; default when it failed.</param>
/// <param name="Error">The exception the work threw, or null.</param>
public sealed record TenantJobOutcome<T>(Guid TenantId, T? Result, Exception? Error);
