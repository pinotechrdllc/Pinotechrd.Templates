using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.BuildingBlocks.Tests.Infrastructure;

// The tenant job runner on the registrations of AddTenantPersistence and a fake directory; no database is reached (the
// shared data source is registered but never resolved). The runner is internal: it is reached through ITenantJobRunner.
public class TenantJobRunnerTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Deliberately not sorted: the runner keeps the directory's order and never reorders.
    private static readonly Guid[] Listed =
    [
        new("c0000000-0000-0000-0000-000000000003"),
        new("a0000000-0000-0000-0000-000000000001"),
        new("b0000000-0000-0000-0000-000000000002"),
    ];

    // The directory port, scoped as the real one is. It records whether its own scope had a tenant.
    private sealed class FakeDirectory(ICurrentTenant tenant, Func<CancellationToken, Task<IReadOnlyList<Guid>>> list) : ITenantDirectory
    {
        public static bool? SawTenant { get; set; }

        public Task<IReadOnlyList<Guid>> ListTenantIdsAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The runner lists the in-service tenants only.");

        public Task<IReadOnlyList<Guid>> ListInServiceTenantIdsAsync(CancellationToken cancellationToken = default)
        {
            SawTenant = tenant.IsSet;
            return list(cancellationToken);
        }
    }

    // A scoped service whose disposal shows when the scope that made it ended.
    private sealed class ScopeProbe : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static ServiceProvider Build(Func<CancellationToken, Task<IReadOnlyList<Guid>>>? list)
    {
        var services = new ServiceCollection();
        services.AddTenantPersistence();
        services.AddScoped(_ => new ScopeProbe());
        if (list is not null)
        {
            services.AddScoped<ITenantDirectory>(sp => new FakeDirectory(sp.GetRequiredService<ICurrentTenant>(), list));
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static ServiceProvider Build(params Guid[] tenants) => Build(_ => Task.FromResult<IReadOnlyList<Guid>>(tenants));

    private static ITenantJobRunner RunnerOf(IServiceProvider services) => services.GetRequiredService<ITenantJobRunner>();

    // The runner's view of a scope's tenant state: whether its connections would assume the tenant's role (false only in a
    // scope the migrator set). Read from the scoped object, an internal type, by its property.
    private static bool AssumesTenantRole(IServiceProvider scope)
    {
        var tenant = scope.GetRequiredService<ICurrentTenant>();
        var property = tenant.GetType().GetProperty("AssumesTenantRole", BindingFlags.Public | BindingFlags.Instance);
        Assert.NotNull(property);
        return (bool)property.GetValue(tenant)!;
    }

    // Breaks when the work is given a scope whose tenant is not the listed one, the tenants are visited out of the
    // directory's order or more than once, two tenants share a scope (the same ICurrentTenant or scoped service), a
    // scope outlives its work, the work is not given the run's token, or the directory is read in a scope with a tenant.
    [Fact]
    public async Task EachTenantRunsInItsOwnScopeWithItsTenantSetInTheDirectorysOrderAndTheScopeIsDisposedAfter()
    {
        using var services = Build(Listed);
        FakeDirectory.SawTenant = null;
        var seen = new List<(Guid Tenant, ICurrentTenant Current, ScopeProbe Probe)>();

        var outcomes = await RunnerOf(services).ForEachTenantAsync(
            (scope, token) =>
            {
                Assert.Equal(Token, token);
                Assert.All(seen, s => Assert.True(s.Probe.Disposed, "The scope of an earlier tenant was not disposed before the next."));
                var current = scope.GetRequiredService<ICurrentTenant>();
                Assert.True(current.IsSet);
                Assert.Equal(TenantSchemaName.For(current.TenantId), current.SchemaName);
                var probe = scope.GetRequiredService<ScopeProbe>();
                seen.Add((current.TenantId, current, probe));
                return Task.FromResult(current.TenantId.ToString("N"));
            },
            Token);

        Assert.False(FakeDirectory.SawTenant);
        Assert.Equal(Listed, seen.Select(s => s.Tenant));
        Assert.Equal(Listed, outcomes.Select(o => o.TenantId));
        Assert.Equal(Listed.Select(t => t.ToString("N")), outcomes.Select(o => o.Result));
        Assert.All(outcomes, o => Assert.Null(o.Error));
        Assert.Equal(Listed.Length, seen.Select(s => s.Current).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(Listed.Length, seen.Select(s => s.Probe).Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.All(seen, s => Assert.True(s.Probe.Disposed));
    }

    // Breaks when one tenant's failure ends the run, is thrown, is not returned as that tenant's Error (the same
    // exception, with no result), or leaves its scope undisposed.
    [Fact]
    public async Task AFailingTenantIsReturnedAsItsErrorAndTheOthersStillRun()
    {
        using var services = Build(Listed);
        var failure = new InvalidOperationException("tenant failure");
        var probes = new List<ScopeProbe>();

        var outcomes = await RunnerOf(services).ForEachTenantAsync(
            (scope, _) =>
            {
                probes.Add(scope.GetRequiredService<ScopeProbe>());
                var tenant = scope.GetRequiredService<ICurrentTenant>().TenantId;
                return tenant == Listed[1] ? throw failure : Task.FromResult(1);
            },
            Token);

        Assert.Equal(Listed, outcomes.Select(o => o.TenantId));
        Assert.Same(failure, outcomes[1].Error);
        Assert.Equal(0, outcomes[1].Result);
        Assert.Equal([1, 1], new[] { outcomes[0].Result, outcomes[2].Result });
        Assert.Null(outcomes[0].Error);
        Assert.Null(outcomes[2].Error);
        Assert.Equal(3, probes.Count);
        Assert.All(probes, p => Assert.True(p.Disposed));
    }

    // Breaks when a work that fails asynchronously (a faulted task, not a synchronous throw) is not returned as its Error.
    [Fact]
    public async Task AWorkThatFaultsAsynchronouslyIsReturnedAsItsError()
    {
        using var services = Build(Listed);

        var outcomes = await RunnerOf(services).ForEachTenantAsync<int>(
            async (scope, token) =>
            {
                await Task.Yield();
                throw new TimeoutException(scope.GetRequiredService<ICurrentTenant>().TenantId.ToString());
            },
            Token);

        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.IsType<TimeoutException>(o.Error));
    }

    // Breaks when a cancellation of the token during the run is swallowed (returned as an Error, or the run goes on to
    // the next tenant) instead of thrown, whether the work stops on it or returns normally after it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ACancellationDuringTheRunIsThrownAndNoFurtherTenantIsVisited(bool workThrows)
    {
        using var services = Build(Listed);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var visited = new List<Guid>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunnerOf(services).ForEachTenantAsync(
            async (scope, token) =>
            {
                visited.Add(scope.GetRequiredService<ICurrentTenant>().TenantId);
                await cancellation.CancelAsync();
                if (workThrows)
                {
                    token.ThrowIfCancellationRequested();
                }

                return 0;
            },
            cancellation.Token));

        Assert.Equal([Listed[0]], visited);
    }

    // Breaks when an already cancelled token still visits a tenant.
    [Fact]
    public async Task AnAlreadyCancelledTokenThrowsAndVisitsNoTenant()
    {
        using var services = Build(_ => Task.FromResult<IReadOnlyList<Guid>>(Listed));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var visits = 0;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RunnerOf(services).ForEachTenantAsync(
            (_, _) => Task.FromResult(++visits), cancellation.Token));

        Assert.Equal(0, visits);
    }

    // Breaks when an OperationCanceledException the work throws while the run's token is not cancelled (a timeout of its
    // own, say) is rethrown or ends the run, instead of being that tenant's Error.
    [Fact]
    public async Task AnOperationCanceledExceptionWithTheTokenNotCancelledIsReturnedAsItsError()
    {
        using var services = Build(Listed);
        var own = new TaskCanceledException("the work's own timeout");

        var outcomes = await RunnerOf(services).ForEachTenantAsync(
            (scope, _) => scope.GetRequiredService<ICurrentTenant>().TenantId == Listed[0] ? throw own : Task.FromResult(2),
            Token);

        Assert.Equal(Listed, outcomes.Select(o => o.TenantId));
        Assert.Same(own, outcomes[0].Error);
        Assert.Equal([2, 2], new[] { outcomes[1].Result, outcomes[2].Result });
    }

    // Breaks when a host without a directory fails, or visits anything, instead of returning no outcome.
    [Fact]
    public async Task WithNoDirectoryRegisteredNoTenantIsVisited()
    {
        using var services = Build(list: null);
        var visits = 0;

        var outcomes = await RunnerOf(services).ForEachTenantAsync((_, _) => Task.FromResult(++visits), Token);

        Assert.Empty(outcomes);
        Assert.Equal(0, visits);
    }

    // Breaks when a failure of the directory is swallowed (an empty list returned) instead of thrown, or a tenant is
    // visited anyway.
    [Fact]
    public async Task AFailureOfTheDirectoryIsThrown()
    {
        var failure = new InvalidOperationException("catalog unreachable");
        using var services = Build(_ => throw failure);
        var visits = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            RunnerOf(services).ForEachTenantAsync((_, _) => Task.FromResult(++visits), Token));

        Assert.Same(failure, thrown);
        Assert.Equal(0, visits);
    }

    // Breaks when the runner sets a scope's tenant with the migration variant (whose connections keep the login instead of
    // assuming the tenant's role) instead of Initialize: setting the same tenant again with Initialize would then throw.
    [Fact]
    public async Task TheWorkNeverRunsInAMigrationScope()
    {
        using var services = Build(Listed);

        var outcomes = await RunnerOf(services).ForEachTenantAsync(
            (scope, _) =>
            {
                var tenant = scope.GetRequiredService<ICurrentTenant>().TenantId;
                scope.GetRequiredService<ICurrentTenantInitializer>().Initialize(tenant);
                return Task.FromResult(AssumesTenantRole(scope));
            },
            Token);

        Assert.All(outcomes, o => Assert.Null(o.Error));
        Assert.All(outcomes, o => Assert.True(o.Result));
    }

    // Guards the test above against being vacuous: in a scope set with the migration variant both observations differ
    // (the property reads false and Initialize with the same tenant throws).
    [Fact]
    public async Task AMigrationScopeIsTellableFromARunnerScope()
    {
        using var services = Build(Listed);
        await using var scope = services.CreateAsyncScope();
        var tenant = scope.ServiceProvider.GetRequiredService<ICurrentTenant>();
        var forMigration = tenant.GetType().GetMethod("InitializeForMigration", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(forMigration);
        forMigration.Invoke(tenant, [Listed[0]]);

        Assert.False(AssumesTenantRole(scope.ServiceProvider));
        Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(Listed[0]));
    }

    // Breaks when a null work is accepted (and fails later, per tenant, as an Error).
    [Fact]
    public async Task ANullWorkIsRefused()
    {
        using var services = Build(Listed);

        await Assert.ThrowsAsync<ArgumentNullException>(() => RunnerOf(services).ForEachTenantAsync<int>(null!, Token));
    }

    // Breaks when AddTenantPersistence does not register the runner as one singleton, or registering twice adds another.
    [Fact]
    public void AddTenantPersistenceRegistersTheRunnerOnceAsASingleton()
    {
        var services = new ServiceCollection();
        services.AddTenantPersistence();
        services.AddTenantPersistence();

        var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ITenantJobRunner));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        using var provider = services.BuildServiceProvider();
        Assert.Same(provider.GetRequiredService<ITenantJobRunner>(), provider.GetRequiredService<ITenantJobRunner>());
    }
}
