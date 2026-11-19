using MyApp.Api.Tests.Database;
using MyApp.Migrator.Tests.Provisioning;
using MyApp.Migrator.Tests.Support;

namespace MyApp.Migrator.Tests.Bootstrap;

// The bootstrap command: the operator tenant, its subscription on the OPERATOR plan, the Administrator role and the first
// administrator, created once whatever the number of runs. Real PostgreSQL server, black-box through the command, with the
// migrator login that production has (not a superuser) where the tenant role matters.
[Collection(PostgresContainer.Name)]
public class BootstrapCommandTests(PostgresFixture postgres)
{
    // Breaks when bootstrap leaves out the active tenant, links its subscription to another plan or by another name than the
    // tenant id, grants the Administrator role fewer permissions than the catalogue, makes no user or no membership, or hashes
    // the password with another format, PRF or iteration count than Identity V3 / HMAC-SHA-512 / 210000.
    [Fact]
    public async Task BootstrapCreatesTheOperatorTenantItsSubscriptionTheAdministratorRoleAndTheAdministratorWithAV3Hash()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await BootstrapSupport.MigrateAsync(logins.MigratorConnection, logins.RoleArgument);

        var run = await BootstrapRun.ExecuteAsync(logins.MigratorConnection, BootstrapRun.GoodPassword, logins.RoleArgument);

        Assert.True(run.ExitCode == 0, run.Report);
        var tenant = Assert.Single(await BootstrapSupport.OperatorTenantsAsync(logins.MigratorConnection));
        Assert.Equal(ProvisionSupport.Active, tenant.Status);
        Assert.Equal(BootstrapRun.DefaultName, tenant.Name);
        Assert.Equal([$"OPERATOR|{tenant.Id:D}|{BootstrapRun.DefaultName}"], await BootstrapSupport.SubscriptionsAsync(logins.MigratorConnection));
        Assert.EndsWith("|Administrator", Assert.Single(await BootstrapSupport.AdministratorRolesAsync(logins.MigratorConnection, tenant.Id)), StringComparison.Ordinal);
        Assert.Equal(BootstrapRun.ExpectedPermissionCodes, await BootstrapSupport.AdministratorPermissionsAsync(logins.MigratorConnection, tenant.Id));
        Assert.Equal([$"{BootstrapRun.DefaultEmail}|{BootstrapRun.DefaultEmail}|true"], await BootstrapSupport.UsersAsync(logins.MigratorConnection, tenant.Id));
        Assert.Equal(1, await BootstrapSupport.MembershipCountAsync(logins.MigratorConnection, tenant.Id));

        var hash = await BootstrapSupport.PasswordHashAsync(logins.MigratorConnection, tenant.Id);
        var header = BootstrapRun.Decode(hash);
        Assert.Equal(0x01, header.Format);
        Assert.Equal(2u, header.Prf); // HMAC-SHA-512
        Assert.Equal(210_000u, header.Iterations);
        Assert.True(header.SaltLength >= 16);
        Assert.Equal(32, header.Subkey.Length);
        Assert.True(BootstrapRun.Verifies(hash, BootstrapRun.GoodPassword));
        Assert.False(BootstrapRun.Verifies(hash, BootstrapRun.GoodPassword + "x"));
        Assert.DoesNotContain(BootstrapRun.GoodPassword, run.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapRun.DefaultEmail, run.Everything, StringComparison.Ordinal);
    }

    // Breaks when a second run changes the hash (a reset password), adds a user, role, tenant, subscription or membership,
    // or fails because everything exists; the different password in the environment must not matter.
    [Fact]
    public async Task ASecondBootstrapWithAnotherPasswordExitsZeroAndChangesNothing()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        Assert.Equal(0, (await BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword)).ExitCode);
        var tenant = await BootstrapSupport.TenantAsync(database);
        var before = await BootstrapSupport.SnapshotAsync(database, tenant);
        var originalHash = await BootstrapSupport.PasswordHashAsync(database, tenant);

        var again = await BootstrapRun.ExecuteAsync(database, "another-long-password-2");

        Assert.True(again.ExitCode == 0, again.Report);
        Assert.Equal(before, await BootstrapSupport.SnapshotAsync(database, tenant));
        Assert.Equal(originalHash, await BootstrapSupport.PasswordHashAsync(database, tenant));
        Assert.True(BootstrapRun.Verifies(originalHash, BootstrapRun.GoodPassword));
        Assert.Single(await BootstrapSupport.OperatorTenantsAsync(database));
        Assert.Equal(1, await BootstrapSupport.OperatorSubscriptionCountAsync(database));
        Assert.DoesNotContain("another-long-password-2", again.Everything, StringComparison.Ordinal);
    }

    // Breaks when concurrent runs both create the tenant (no lock on the catalog step), insert a second subscription, user or
    // membership, or one of them fails on the unique constraint instead of finding the rows.
    [Fact]
    public async Task ConcurrentBootstrapRunsAllExitZeroAndLeaveOneTenantOneSubscriptionOneAdministratorAndOneMembership()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        string[] Arguments() => [BootstrapRun.Verb, $"--{MigratorRun.ConnectionKey}={database}", .. BootstrapRun.Settings()];

        var runs = await BootstrapRun.ExecuteAllAsync(BootstrapRun.GoodPassword, Arguments(), Arguments(), Arguments());

        Assert.All(runs, r => Assert.True(r.ExitCode == 0, r.Report));
        var tenant = await BootstrapSupport.TenantAsync(database);
        Assert.Equal(1, await BootstrapSupport.OperatorSubscriptionCountAsync(database));
        Assert.Single(await BootstrapSupport.SubscriptionsAsync(database));
        Assert.Single(await BootstrapSupport.UsersAsync(database, tenant));
        Assert.Single(await BootstrapSupport.AdministratorRolesAsync(database, tenant));
        Assert.Equal(1, await BootstrapSupport.MembershipCountAsync(database, tenant));
        Assert.Equal(BootstrapRun.ExpectedPermissionCodes, await BootstrapSupport.AdministratorPermissionsAsync(database, tenant));
        Assert.Equal(1, await Db.CountAsync(database, "SELECT count(*) FROM catalog.tenants WHERE identifier = '" + BootstrapRun.DefaultSlug + "'"));
    }

    // Breaks when the command starts without a password and still makes the administrator (empty password), or reports an
    // error that does not say which setting to set.
    [Fact]
    public async Task WithoutAPasswordAndWithoutAnAdministratorBootstrapExitsTwoAndNamesTheSetting()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);

        var run = await BootstrapRun.ExecuteAsync(database, password: null);

        Assert.Equal(MigratorHost.InvalidSettings, run.ExitCode);
        Assert.Contains("AdminPassword", run.Report, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapRun.DefaultEmail, run.Everything, StringComparison.Ordinal);
        foreach (var tenant in await BootstrapSupport.OperatorTenantsAsync(database))
        {
            Assert.Empty(await BootstrapSupport.UsersAsync(database, tenant.Id));
        }
    }

    // Breaks when a run without a password fails although the administrator exists (the password is needed only to create).
    [Fact]
    public async Task WithoutAPasswordBootstrapExitsZeroWhenTheAdministratorAlreadyExists()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        Assert.Equal(0, (await BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword)).ExitCode);
        var tenant = await BootstrapSupport.TenantAsync(database);
        var before = await BootstrapSupport.SnapshotAsync(database, tenant);

        var run = await BootstrapRun.ExecuteAsync(database, password: null);

        Assert.True(run.ExitCode == 0, run.Report);
        Assert.Equal(before, await BootstrapSupport.SnapshotAsync(database, tenant));
    }
}
