using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Tenancy;

// The current tenant of a scope, through the public contract. No database: only the tenant services are registered.
public class CurrentTenantTests
{
    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddTenantPersistence();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    // Breaks when a new scope reports a tenant, or when reading the id or the schema of an unset tenant does not throw.
    [Fact]
    public void ANewScopeHasNoTenantAndReadingItThrows()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

        Assert.False(tenant.IsSet);
        Assert.Throws<InvalidOperationException>(() => tenant.TenantId);
        Assert.Throws<InvalidOperationException>(() => tenant.SchemaName);
    }

    // Breaks when Initialize does not set the id and its derived schema.
    [Fact]
    public void InitializeSetsTheTenantAndItsSchema()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var id = Guid.NewGuid();

        scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(id);

        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        Assert.True(tenant.IsSet);
        Assert.Equal(id, tenant.TenantId);
        Assert.Equal(TenantSchemaName.For(id), tenant.SchemaName);
    }

    // Breaks when initialising twice with the same tenant throws or changes the tenant.
    [Fact]
    public void InitializingTheSameTenantTwiceIsANoOp()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var id = Guid.NewGuid();
        var initializer = scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>();

        initializer.Initialize(id);
        initializer.Initialize(id);

        Assert.Equal(id, scope.ServiceProvider.GetRequiredService<ICurrentTenant>().TenantId);
    }

    // Breaks when a scope can switch tenants halfway, or when a refused switch changes the tenant anyway.
    [Fact]
    public void InitializingAnotherTenantThrowsAndKeepsTheFirst()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var first = Guid.NewGuid();
        var initializer = scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>();
        initializer.Initialize(first);

        Assert.Throws<InvalidOperationException>(() => initializer.Initialize(Guid.NewGuid()));

        Assert.Equal(first, scope.ServiceProvider.GetRequiredService<ICurrentTenant>().TenantId);
    }

    // Breaks when the empty id is accepted, and when the rejection leaves the scope with a tenant.
    [Fact]
    public void TheEmptyIdIsRejectedAndLeavesTheScopeUnset()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();

        Assert.Throws<ArgumentException>(() => scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(Guid.Empty));

        Assert.False(scope.ServiceProvider.GetRequiredService<ICurrentTenant>().IsSet);
    }

    // Breaks when the tenant is shared between scopes (a singleton, a static or an ambient value).
    [Fact]
    public void TwoScopesAreIndependentAndALaterScopeStartsUnset()
    {
        using var provider = Provider();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        using var first = provider.CreateScope();
        using var second = provider.CreateScope();

        first.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(a);
        second.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(b);

        Assert.Equal(a, first.ServiceProvider.GetRequiredService<ICurrentTenant>().TenantId);
        Assert.Equal(b, second.ServiceProvider.GetRequiredService<ICurrentTenant>().TenantId);
        using var third = provider.CreateScope();
        Assert.False(third.ServiceProvider.GetRequiredService<ICurrentTenant>().IsSet);
    }

    // Breaks when the reader and the initializer of one scope are different objects (a tenant set through one would not
    // be seen through the other), or when one scope hands out several.
    [Fact]
    public void ReaderAndInitializerOfAScopeShareTheSameState()
    {
        using var provider = Provider();
        using var scope = provider.CreateScope();
        var reader = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();

        scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(Guid.NewGuid());

        Assert.True(reader.IsSet);
        Assert.Same(reader, scope.ServiceProvider.GetRequiredService<ICurrentTenant>());
    }

    // Breaks when concurrent scopes leak into each other: each task sets its own tenant and must read it back.
    [Fact]
    public async Task ParallelScopesNeverSeeEachOthersTenant()
    {
        using var provider = Provider();

        await Parallel.ForEachAsync(
            Enumerable.Range(0, 200),
            new ParallelOptions { MaxDegreeOfParallelism = 16, CancellationToken = TestContext.Current.CancellationToken },
            async (_, _) =>
            {
                var id = Guid.NewGuid();
                using var scope = provider.CreateScope();
                scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(id);
                await Task.Yield();
                Assert.Equal(id, scope.ServiceProvider.GetRequiredService<ICurrentTenant>().TenantId);
            });
    }

    // Breaks when registering the tenant services twice replaces them or registers a second set.
    [Fact]
    public void RegisteringTenantPersistenceTwiceHasNoFurtherEffect()
    {
        var services = new ServiceCollection();
        services.AddTenantPersistence();
        var count = services.Count;

        services.AddTenantPersistence();

        Assert.Equal(count, services.Count);
    }
}
