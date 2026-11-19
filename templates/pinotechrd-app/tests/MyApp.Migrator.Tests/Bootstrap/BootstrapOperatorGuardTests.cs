using MyApp.Api.Tests.Database;
using MyApp.Migrator.Tests.Provisioning;
using MyApp.Migrator.Tests.Support;

namespace MyApp.Migrator.Tests.Bootstrap;

// The operator subscription belongs to one tenant: a second slug never becomes a second tenant on it. Settings the Access
// module's rules would refuse (user name characters, whitespace-only password) are refused before anything is written, and
// a second administrator's email never creates a second user. Real PostgreSQL, black-box through the command.
[Collection(PostgresContainer.Name)]
public class BootstrapOperatorGuardTests(PostgresFixture postgres)
{
    private static string[] ArgumentsFor(string database, string slug) =>
        [BootstrapRun.Verb, $"--{MigratorRun.ConnectionKey}={database}", .. BootstrapRun.Settings(slug: slug, name: "Op")];

    private static Task<long> PlanCountAsync(string database) => Db.CountAsync(database, "SELECT count(*) FROM catalog.subscription_plans");

    // The plans a migrated database already has (migrate seeds them) must not change; no subscription, tenant or schema appears.
    private static async Task AssertNothingWrittenAsync(string database, long plansBefore)
    {
        Assert.Equal(0, await Db.CountAsync(database, "SELECT count(*) FROM catalog.tenants"));
        Assert.Equal(0, await Db.CountAsync(database, "SELECT count(*) FROM catalog.system_subscriptions"));
        Assert.Equal(plansBefore, await PlanCountAsync(database));
        Assert.Equal(0, await ProvisionSupport.TenantSchemaCountAsync(database));
    }

    // Breaks when two runs with different slugs both link a tenant to the operator subscription (no atomic check before the
    // provisioning), when the loser fails after provisioning its tenant, or when both fail.
    [Fact]
    public async Task ConcurrentBootstrapsOfDifferentSlugsLeaveExactlyOneTenantOnTheOperatorSubscriptionAndTheOtherProvisionsNothing()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);

        var runs = await BootstrapRun.ExecuteAllAsync(BootstrapRun.GoodPassword, ArgumentsFor(database, "op-a"), ArgumentsFor(database, "op-b"));

        var rows = await ProvisionSupport.RowsAsync(database);
        var winner = Assert.Single(rows);
        Assert.Equal(1, await Db.CountAsync(database, "SELECT count(*) FROM catalog.tenants t JOIN catalog.system_subscriptions s ON s.id = t.subscription_id WHERE s.tenant_name = t.id::text"));
        Assert.Equal(1, await BootstrapSupport.OperatorSubscriptionCountAsync(database));
        Assert.Equal(1, runs.Count(r => r.ExitCode == 0));
        Assert.Equal(1, runs.Count(r => r.ExitCode != 0));
        var winnerSlug = (await Db.ColumnAsync(database, "SELECT identifier FROM catalog.tenants")).Single();
        var loserSlug = winnerSlug == "op-a" ? "op-b" : "op-a";
        Assert.Empty(await ProvisionSupport.RowsAsync(database, loserSlug));
        Assert.Equal(1L, await ProvisionSupport.TenantSchemaCountAsync(database));
        Assert.Equal(winner.Id, Guid.Parse(Assert.Single(await Db.ColumnAsync(database, "SELECT tenant_name FROM catalog.system_subscriptions"))));
    }

    // Breaks when a second slug is accepted after the first one holds the operator subscription, or the refusal writes a
    // tenant row or schema for it first.
    [Fact]
    public async Task ABootstrapOfAnotherSlugAfterASuccessfulOneFailsAndCreatesNoTenant()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var first = (await BootstrapRun.ExecuteAllAsync(BootstrapRun.GoodPassword, ArgumentsFor(database, "op-a")))[0];
        Assert.True(first.ExitCode == 0, first.Report);
        var before = await Db.ColumnAsync(database, "SELECT id || '|' || identifier || '|' || subscription_id FROM catalog.tenants");

        var second = (await BootstrapRun.ExecuteAllAsync(BootstrapRun.GoodPassword, ArgumentsFor(database, "op-b")))[0];

        Assert.NotEqual(0, second.ExitCode);
        Assert.Empty(await ProvisionSupport.RowsAsync(database, "op-b"));
        Assert.Equal(before, await Db.ColumnAsync(database, "SELECT id || '|' || identifier || '|' || subscription_id FROM catalog.tenants"));
        Assert.Equal(1L, await ProvisionSupport.TenantSchemaCountAsync(database));
        Assert.DoesNotContain(BootstrapRun.GoodPassword, second.Everything, StringComparison.Ordinal);
    }

    // Breaks when an email the Access user rules refuse (apostrophe, characters outside the allowed user name set) is checked
    // only after the catalog and tenant were written, exits 1 instead of 2, or is quoted in the report or on standard error.
    [Theory]
    [InlineData("o'brien@example.test", "brien")]
    [InlineData("a!b@example.test", "a!b")]
    [InlineData("müller@example.test", "ller@")]
    [InlineData("a,b@example.test", "a,b")]
    public async Task AnEmailIdentityWouldRefuseAsAUserNameExitsTwoWritesNothingAndIsNeverEchoed(string email, string fragment)
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var plans = await PlanCountAsync(database);

        var run = (await BootstrapRun.ExecuteAllAsync(
            BootstrapRun.GoodPassword,
            [BootstrapRun.Verb, $"--{MigratorRun.ConnectionKey}={database}", .. BootstrapRun.Settings(email: email)]))[0];

        Assert.Equal(MigratorHost.InvalidSettings, run.ExitCode);
        Assert.DoesNotContain(email, run.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(fragment, run.Everything, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(database, plans);
    }

    // Breaks when a password of 15 whitespace characters counts as a password (length only), is refused after writing, or is
    // echoed.
    [Theory]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" ")]
    [InlineData("　")]
    public async Task AWhitespaceOnlyPasswordOfFifteenRunesExitsTwoWritesNothingAndIsNeverEchoed(string unit)
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var password = string.Concat(Enumerable.Repeat(unit, 15));
        var plans = await PlanCountAsync(database);

        var run = await BootstrapRun.ExecuteAsync(database, password);

        Assert.Equal(MigratorHost.InvalidSettings, run.ExitCode);
        Assert.DoesNotContain(password, run.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapRun.DefaultEmail, run.Everything, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(database, plans);
    }

    // Breaks when a rerun with another email adds a second administrator (or is accepted silently), changes the first one,
    // or quotes either email in the report or on standard error.
    [Fact]
    public async Task ARerunWithAnotherAdminEmailExitsOneCreatesNoSecondUserAndEchoesNoEmail()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var first = await BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword);
        Assert.True(first.ExitCode == 0, first.Report);
        var tenant = await BootstrapSupport.TenantAsync(database);
        var before = await BootstrapSupport.SnapshotAsync(database, tenant);
        const string Other = "second.admin@example.test";

        var second = (await BootstrapRun.ExecuteAllAsync(
            BootstrapRun.GoodPassword,
            [BootstrapRun.Verb, $"--{MigratorRun.ConnectionKey}={database}", .. BootstrapRun.Settings(email: Other)]))[0];

        Assert.Equal(MigratorHost.MigrationFailed, second.ExitCode);
        Assert.Equal([$"{BootstrapRun.DefaultEmail}|{BootstrapRun.DefaultEmail}|true"], await BootstrapSupport.UsersAsync(database, tenant));
        Assert.Equal(before, await BootstrapSupport.SnapshotAsync(database, tenant));
        Assert.DoesNotContain(Other, second.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapRun.DefaultEmail, second.Everything, StringComparison.Ordinal);
    }

    // Breaks when the rerun guard against another email also rejects the same email (idempotence lost).
    [Fact]
    public async Task ARerunWithTheSameAdminEmailStillExitsZero()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        Assert.Equal(0, (await BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword)).ExitCode);

        var again = await BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword);

        Assert.True(again.ExitCode == 0, again.Report);
        Assert.Single(await BootstrapSupport.UsersAsync(database, await BootstrapSupport.TenantAsync(database)));
    }
}
