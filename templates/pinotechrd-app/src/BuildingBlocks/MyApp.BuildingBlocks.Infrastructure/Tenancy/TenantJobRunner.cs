using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Infrastructure.Migrations;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>The <see cref="ITenantJobRunner"/>.</summary>
/// <param name="scopeFactory">Creates the scope of the directory read and one scope per tenant.</param>
internal sealed class TenantJobRunner(IServiceScopeFactory scopeFactory) : ITenantJobRunner
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TenantJobOutcome<T>>> ForEachTenantAsync<T>(
        Func<IServiceProvider, CancellationToken, Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);

        var tenantIds = await ListTenantsAsync(cancellationToken).ConfigureAwait(false);
        var outcomes = new List<TenantJobOutcome<T>>(tenantIds.Count);
        foreach (var tenantId in tenantIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var scope = scopeFactory.CreateAsyncScope();
                await using (scope.ConfigureAwait(false))
                {
                    scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(tenantId);
                    var result = await work(scope.ServiceProvider, cancellationToken).ConfigureAwait(false);
                    outcomes.Add(new TenantJobOutcome<T>(tenantId, result, Error: null));
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                outcomes.Add(new TenantJobOutcome<T>(tenantId, default, exception));
            }
        }

        return outcomes;
    }

    private async Task<IReadOnlyList<Guid>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        var scope = scopeFactory.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var directory = scope.ServiceProvider.GetService<ITenantDirectory>();
            return directory is null
                ? []
                : await directory.ListInServiceTenantIdsAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
