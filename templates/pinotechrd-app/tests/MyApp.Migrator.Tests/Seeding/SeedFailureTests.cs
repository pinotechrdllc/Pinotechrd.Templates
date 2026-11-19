using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Application.Security;
using MyApp.Modules.Access.Infrastructure.Seeding;

namespace MyApp.Migrator.Tests.Seeding;

// Tenants whose hand-edited security rows cannot be reconciled with the code's catalogue: the run must fail that tenant
// only, say so clearly, and leave its rows as they were. Real PostgreSQL server, black-box through the command.
[Collection(PostgresContainer.Name)]
public class SeedFailureTests(PostgresFixture postgres)
{
    private static readonly string[] ExpectedPermissionCodes = [.. AccessPermissions.All.Select(p => p.ToUpperInvariant()).Order(StringComparer.Ordinal)];

    private static string Schema(Guid tenant) => TenantSchemaName.For(tenant);

    private async Task<(string Database, Guid Bad, Guid Good)> TwoMigratedTenantsAsync()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var (bad, good) = (Guid.NewGuid(), Guid.NewGuid());
        await CatalogSeed.AddTenantAsync(database, bad);
        await CatalogSeed.AddTenantAsync(database, good);
        var run = await MigratorRun.ExecuteAsync(database);
        Assert.True(run.ExitCode == 0, run.Report);
        return (database, bad, good);
    }

    // Removes the newest permission of a tenant so that only a seeding run can bring it back.
    private static async Task MakeNeedSeedingAsync(string database, Guid tenant)
    {
        var schema = Schema(tenant);
        var id = SeedIds.Permission(ExpectedPermissionCodes[^1]);
        await Db.ExecuteAsync(
            database,
            $"DELETE FROM \"{schema}\".role_permissions WHERE permission_id = '{id}'; DELETE FROM \"{schema}\".security_permissions WHERE id = '{id}'");
    }

    private static async Task<List<string>> SecurityRowsAsync(string database, Guid tenant)
    {
        var schema = Schema(tenant);
        var rows = await Db.ColumnAsync(database, $"SELECT 'M|' || id || '|' || code FROM \"{schema}\".security_modules ORDER BY 1");
        rows.AddRange(await Db.ColumnAsync(database, $"SELECT 'P|' || id || '|' || code || '|' || module_id FROM \"{schema}\".security_permissions ORDER BY 1"));
        return rows;
    }

    private static async Task AssertFullySeededAsync(string database, Guid tenant) =>
        Assert.Equal(
            ExpectedPermissionCodes,
            await Db.ColumnAsync(database, $"SELECT code FROM \"{Schema(tenant)}\".security_permissions ORDER BY code COLLATE \"C\""));

    private static void AssertNamesTenantAsFailed(string report, Guid tenant)
    {
        var line = Assert.Single(report.Split('\n'), l => l.Contains(tenant.ToString("D"), StringComparison.OrdinalIgnoreCase));
        Assert.Matches("(?i)fail|error", line);
    }

    // Breaks when a module row with the catalogue code but a non-deterministic id is adopted silently (plans point at the
    // deterministic id), the failure does not name the tenant and the module, the tenant's rows are partly written, or the
    // healthy tenant of the same run is not seeded.
    [Fact]
    public async Task ATenantWhoseAccessModuleHasARandomIdFailsWithExitOneNamedInTheReportKeepsItsRowsAndDoesNotStopAnotherTenant()
    {
        var (database, bad, good) = await TwoMigratedTenantsAsync();
        var schema = Schema(bad);
        await MakeNeedSeedingAsync(database, bad);
        await MakeNeedSeedingAsync(database, good);
        await Db.ExecuteAsync(
            database,
            $"DELETE FROM \"{schema}\".role_permissions; DELETE FROM \"{schema}\".security_permissions; DELETE FROM \"{schema}\".security_modules; "
            + $"INSERT INTO \"{schema}\".security_modules (id, name, code, is_active, display_order, created_at, tenant_id) "
            + $"VALUES (gen_random_uuid(), 'Access', 'ACCESS', true, 0, now(), '{bad:D}')");
        var before = await SecurityRowsAsync(database, bad);

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.Equal(1, run.ExitCode);
        AssertNamesTenantAsFailed(run.Report, bad);
        Assert.Contains("ACCESS", run.Report, StringComparison.Ordinal);
        Assert.Equal(before, await SecurityRowsAsync(database, bad));
        await AssertFullySeededAsync(database, good);
    }

    // Breaks when a deterministic module row that was re-coded by hand ends in a raw KeyNotFoundException instead of a clear
    // message, makes the run succeed, or takes the other tenant down with it.
    [Fact]
    public async Task ATenantWhoseDeterministicAccessModuleWasRecodedFailsWithExitOneAndAClearMessageWhileAnotherTenantIsSeeded()
    {
        var (database, bad, good) = await TwoMigratedTenantsAsync();
        await MakeNeedSeedingAsync(database, good);
        await Db.ExecuteAsync(database, $"UPDATE \"{Schema(bad)}\".security_modules SET code = 'ACCESSOLD' WHERE code = 'ACCESS'");
        var before = await SecurityRowsAsync(database, bad);

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.Equal(1, run.ExitCode);
        AssertNamesTenantAsFailed(run.Report, bad);
        Assert.DoesNotContain("KeyNotFoundException", run.Report, StringComparison.Ordinal);
        Assert.Contains("ACCESS", run.Report, StringComparison.Ordinal);
        Assert.Equal(before, await SecurityRowsAsync(database, bad));
        await AssertFullySeededAsync(database, good);
    }

    // Breaks when a permission row with the catalogue code but a random id is accepted (two rows or a stale id for one
    // right) or fails without naming the tenant and the permission code.
    [Fact]
    public async Task ATenantWithAPermissionOfARandomIdFailsWithAClearMessageNamingTheTenantAndThePermissionCode()
    {
        var (database, bad, good) = await TwoMigratedTenantsAsync();
        await MakeNeedSeedingAsync(database, good);
        await Db.ExecuteAsync(
            database,
            $"UPDATE \"{Schema(bad)}\".security_permissions SET id = gen_random_uuid() WHERE code = 'ACCESS.USERS.READ'");
        var before = await SecurityRowsAsync(database, bad);

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.Equal(1, run.ExitCode);
        AssertNamesTenantAsFailed(run.Report, bad);
        Assert.Contains("ACCESS.USERS.READ", run.Report, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyNotFoundException", run.Report, StringComparison.Ordinal);
        Assert.Equal(before, await SecurityRowsAsync(database, bad));
        await AssertFullySeededAsync(database, good);
    }

    // Breaks when one tenant whose seeding fails stops the run before the tenants that are still to be created and seeded,
    // or lets the run end with exit 0.
    [Fact]
    public async Task OneTenantFailingToSeedGivesExitOneWhileEveryOtherTenantIsMigratedAndSeeded()
    {
        var (database, bad, _) = await TwoMigratedTenantsAsync();
        await Db.ExecuteAsync(database, $"UPDATE \"{Schema(bad)}\".security_modules SET code = 'ACCESSOLD' WHERE code = 'ACCESS'");
        var (fresh1, fresh2) = (Guid.NewGuid(), Guid.NewGuid());
        await CatalogSeed.AddTenantAsync(database, fresh1);
        await CatalogSeed.AddTenantAsync(database, fresh2);

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.Equal(1, run.ExitCode);
        AssertNamesTenantAsFailed(run.Report, bad);
        foreach (var tenant in new[] { fresh1, fresh2 })
        {
            Assert.DoesNotMatch("(?i)fail", Assert.Single(run.Report.Split('\n'), l => l.Contains(tenant.ToString("D"), StringComparison.OrdinalIgnoreCase)));
            await AssertFullySeededAsync(database, tenant);
        }
    }
}
