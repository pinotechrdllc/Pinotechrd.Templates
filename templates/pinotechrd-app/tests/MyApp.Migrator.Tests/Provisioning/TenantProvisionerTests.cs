using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Domain;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Application.Tenants;

namespace MyApp.Migrator.Tests.Provisioning;

// The ITenantProvisioner port resolved from the migrator's composition, with real parallel callers.
[Collection(PostgresContainer.Name)]
public class TenantProvisionerTests(PostgresFixture postgres)
{
    private const int Callers = 8;

    private static async Task<Result<ProvisionedTenant>> ProvisionAsync(ProvisioningHost host, ProvisionTenantRequest request)
    {
        await using var scope = host.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantProvisioner>()
            .ProvisionAsync(request, TestContext.Current.CancellationToken);
    }

    // Every task has its own scope and provisioner before any of them calls, then all call at the same moment.
    private static async Task<Result<ProvisionedTenant>[]> ProvisionTogetherAsync(ProvisioningHost host, params ProvisionTenantRequest[] requests)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = 0;
        var tasks = requests.Select(request => Task.Run(
            async () =>
            {
                await using var scope = host.Services.CreateAsyncScope();
                var provisioner = scope.ServiceProvider.GetRequiredService<ITenantProvisioner>();
                if (Interlocked.Increment(ref arrived) == requests.Length)
                {
                    gate.SetResult();
                }

                await gate.Task;
                return await provisioner.ProvisionAsync(request, TestContext.Current.CancellationToken);
            },
            TestContext.Current.CancellationToken)).ToArray();
        return await Task.WhenAll(tasks);
    }

    private static string Describe(Result result) => result.IsFailure ? result.Error.ToString() : string.Empty;

    // Breaks when concurrent provisions of one slug fail, create two rows or two ids, migrate a schema twice or do not end Active.
    [Fact]
    public async Task EightConcurrentProvisionsOfTheSameSlugGiveOneActiveTenantMigratedOnce()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        await using var host = await ProvisioningHost.StartAsync(database);
        var baseline = await ProvisionAsync(host, new ProvisionTenantRequest("baseline", "Baseline", subscription));
        Assert.True(baseline.IsSuccess, Describe(baseline));
        var expectedHistory = await ProvisionSupport.HistoryAsync(database, baseline.Value.TenantId);
        var request = new ProvisionTenantRequest("acme", "Acme", subscription);

        var results = await ProvisionTogetherAsync(host, [.. Enumerable.Repeat(request, Callers)]);

        Assert.All(results, r => Assert.True(r.IsSuccess, Describe(r)));
        var id = Assert.Single(results.Select(r => r.Value.TenantId).Distinct());
        Assert.Equal(1, results.Count(r => !r.Value.AlreadyProvisioned));
        Assert.Equal(Callers - 1, results.Count(r => r.Value.AlreadyProvisioned));
        var row = Assert.Single(await ProvisionSupport.RowsAsync(database, "acme"));
        Assert.Equal(1, row.Attempts);
        Assert.Equal(id, row.Id);
        Assert.Equal(ProvisionSupport.Active, row.Status);
        var history = await ProvisionSupport.HistoryAsync(database, id);
        Assert.Equal(history.Distinct().Count(), history.Count);
        Assert.Equal(expectedHistory, history);
        Assert.Equal(2, (await ProvisionSupport.RowsAsync(database)).Count);
    }

    // Breaks when two different slugs started together interfere: a lost row, a shared schema or a failure.
    [Fact]
    public async Task TwoConcurrentProvisionsOfDifferentSlugsGiveTwoTenantsWithTwoSchemas()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        await using var host = await ProvisioningHost.StartAsync(database);

        var results = await ProvisionTogetherAsync(
            host, new ProvisionTenantRequest("acme", "Acme", subscription), new ProvisionTenantRequest("globex", "Globex", subscription));

        Assert.All(results, r => Assert.True(r.IsSuccess, Describe(r)));
        var rows = await ProvisionSupport.RowsAsync(database);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row => Assert.Equal(ProvisionSupport.Active, row.Status));
        Assert.Equal(2, rows.Select(r => r.Id).Distinct().Count());
        foreach (var row in rows)
        {
            Assert.Contains("users", await Db.TablesOfAsync(database, TenantSchemaName.For(row.Id)));
        }
    }

    // Breaks when concurrent requests that disagree on the name both succeed, or the winner's row is changed.
    [Fact]
    public async Task ConcurrentRequestsForOneSlugWithDifferentNamesGiveExactlyOneWinnerAndSlugTakenForTheOther()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        await using var host = await ProvisioningHost.StartAsync(database);

        var results = await ProvisionTogetherAsync(
            host, new ProvisionTenantRequest("acme", "First", subscription), new ProvisionTenantRequest("acme", "Second", subscription));

        var winner = Assert.Single(results, r => r.IsSuccess);
        var loser = Assert.Single(results, r => r.IsFailure);
        Assert.Equal(TenantErrorCodes.SlugTaken, loser.Error.Code);
        var row = Assert.Single(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(winner.Value.TenantId, row.Id);
        Assert.Equal(ProvisionSupport.Active, row.Status);
    }

    // Breaks when the port reports a repeat as a new provisioning, or a first call as already provisioned.
    [Fact]
    public async Task ARepeatedRequestReportsAlreadyProvisionedWithTheSameTenantId()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        await using var host = await ProvisioningHost.StartAsync(database);
        var request = new ProvisionTenantRequest("acme", "Acme", subscription);

        var first = await ProvisionAsync(host, request);
        var second = await ProvisionAsync(host, request);

        Assert.True(first.IsSuccess, Describe(first));
        Assert.False(first.Value.AlreadyProvisioned);
        Assert.True(second.IsSuccess, Describe(second));
        Assert.True(second.Value.AlreadyProvisioned);
        Assert.Equal(first.Value.TenantId, second.Value.TenantId);
        Assert.Equal("acme", second.Value.Slug);
    }

    // Breaks when the expected failures are not results with their codes (a throw, or another code).
    [Fact]
    public async Task ExpectedFailuresAreResultsWithTheirErrorCodes()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);
        await using var host = await ProvisioningHost.StartAsync(database);
        await CatalogSeed.AddTenantRowAsync(database, Guid.NewGuid(), subscription, "frozen", "Frozen", ProvisionSupport.Suspended);
        Assert.True((await ProvisionAsync(host, new ProvisionTenantRequest("acme", "Acme", subscription))).IsSuccess);

        var unknown = await ProvisionAsync(host, new ProvisionTenantRequest("other", "Other", Guid.NewGuid()));
        var taken = await ProvisionAsync(host, new ProvisionTenantRequest("acme", "Renamed", subscription));
        var frozen = await ProvisionAsync(host, new ProvisionTenantRequest("frozen", "Frozen", subscription));

        Assert.True(unknown.IsFailure);
        Assert.Equal(TenantErrorCodes.SubscriptionNotFound, unknown.Error.Code);
        Assert.True(taken.IsFailure);
        Assert.Equal(TenantErrorCodes.SlugTaken, taken.Error.Code);
        Assert.True(frozen.IsFailure);
        Assert.Equal(TenantErrorCodes.Suspended, frozen.Error.Code);
        Assert.Empty(await ProvisionSupport.RowsAsync(database, "other"));
    }
}
