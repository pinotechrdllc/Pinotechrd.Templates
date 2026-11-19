using MyApp.Api.Tests.Database;
using MyApp.Migrator.Tests.Bootstrap;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Seeding;

namespace MyApp.Migrator.Tests.Provisioning;

// The operator subscription is the bootstrap command's: provision never puts another tenant on it.
[Collection(PostgresContainer.Name)]
public class ProvisionOperatorSubscriptionTests(PostgresFixture postgres)
{
    // Breaks when provision accepts the operator subscription for a second tenant, writes a tenant row or schema before
    // refusing, exits 0, or repeats the subscription id in its report.
    [Fact]
    public async Task ProvisionOnTheOperatorSubscriptionIsRefusedCreatesNoTenantAndDoesNotEchoTheId()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var bootstrap = await BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword);
        Assert.True(bootstrap.ExitCode == 0, bootstrap.Report);
        var subscription = SeedIds.OperatorSubscription;

        var run = await ProvisionSupport.RunAsync(database, "acme", "Acme", subscription);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(await ProvisionSupport.RowsAsync(database, "acme"));
        Assert.Equal(1L, await ProvisionSupport.TenantSchemaCountAsync(database));
        Assert.DoesNotContain(subscription.ToString("D"), run.Report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(subscription.ToString("N"), run.Report, StringComparison.OrdinalIgnoreCase);
    }

    // Breaks when the refusal depends on the operator subscription already being linked: with the catalog only migrated, the
    // operator subscription id must still not provision a tenant.
    [Fact]
    public async Task ProvisionOnTheOperatorSubscriptionIdBeforeAnyBootstrapCreatesNoTenant()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, "acme", "Acme", SeedIds.OperatorSubscription);

        Assert.NotEqual(0, run.ExitCode);
        Assert.Empty(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(0L, await ProvisionSupport.TenantSchemaCountAsync(database));
        Assert.DoesNotContain(SeedIds.OperatorSubscription.ToString("D"), run.Report, StringComparison.OrdinalIgnoreCase);
    }
}
