using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Application.Security;
using MyApp.Modules.Access.Infrastructure.Seeding;
using Npgsql;
using Pinotechrd.Security.Core;

namespace MyApp.Migrator.Tests.Seeding;

// What the migrate command keeps in sync with the code: the catalog's plans and, in every tenant schema, the modules and
// permissions of the catalogue and the Administrator role's rights. Real PostgreSQL server, black-box through the command.
[Collection(PostgresContainer.Name)]
public class SeedOnMigrateTests(PostgresFixture postgres)
{
    private static readonly string[] ExpectedPermissionCodes = [.. AccessPermissions.All.Select(p => p.ToUpperInvariant()).Order(StringComparer.Ordinal)];

    private static readonly string[] ExpectedModuleCodes = [.. ExpectedPermissionCodes.Select(c => c[..c.IndexOf('.', StringComparison.Ordinal)]).Distinct().Order(StringComparer.Ordinal)];

    private static string Schema(Guid tenant) => TenantSchemaName.For(tenant);

    private static async Task<string> MigratedTenantAsync(string database, Guid tenant)
    {
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        await CatalogSeed.AddTenantAsync(database, tenant);
        var run = await MigratorRun.ExecuteAsync(database);
        Assert.True(run.ExitCode == 0, run.Report);
        return Schema(tenant);
    }

    private static Task<List<string>> ModuleRowsAsync(string database, string schema) =>
        Db.ColumnAsync(database, $"SELECT id || '|' || code FROM \"{schema}\".security_modules ORDER BY code COLLATE \"C\"");

    private static Task<List<string>> PermissionRowsAsync(string database, string schema) =>
        Db.ColumnAsync(database, $"SELECT id || '|' || code || '|' || module_id FROM \"{schema}\".security_permissions ORDER BY code COLLATE \"C\"");

    private const string PlanRows =
        "SELECT p.id || '|' || p.code FROM catalog.subscription_plans p WHERE p.code IN ('STANDARD', 'OPERATOR') ORDER BY p.code COLLATE \"C\"";

    private const string PlanModuleRows =
        "SELECT p.code || '|' || m.id || '|' || m.module_id FROM catalog.subscription_plan_modules m "
        + "JOIN catalog.subscription_plans p ON p.id = m.plan_id ORDER BY p.code, m.module_id";

    // Breaks when migrate leaves a tenant without a module row of the catalogue (id from SecurityModuleId.FromCode, upper-case
    // code) or without a permission row (code ACCESS.USERS.READ, id from SeedIds.Permission, tied to its module), seeds a
    // tenant only on its first migration, or seeds extra rows.
    [Fact]
    public async Task MigrateSeedsEveryCatalogueModuleAndPermissionIntoEveryTenantSchemaWithDeterministicIds()
    {
        var database = await postgres.CreateDatabaseAsync();
        var (first, second) = (Guid.NewGuid(), Guid.NewGuid());
        await MigratedTenantAsync(database, first);
        await CatalogSeed.AddTenantAsync(database, second);

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.True(run.ExitCode == 0, run.Report);
        foreach (var schema in new[] { Schema(first), Schema(second) })
        {
            Assert.Equal(
                [.. ExpectedModuleCodes.Select(c => $"{SecurityModuleId.FromCode(c)}|{c}")],
                await ModuleRowsAsync(database, schema));
            Assert.Equal(
                [.. ExpectedPermissionCodes.Select(c => $"{SeedIds.Permission(c)}|{c}|{SecurityModuleId.FromCode(c[..c.IndexOf('.', StringComparison.Ordinal)])}")],
                await PermissionRowsAsync(database, schema));
        }

        Assert.Contains("ACCESS.USERS.READ", ExpectedPermissionCodes);
    }

    // Breaks when a second migrate inserts a duplicate, replaces an id, or fails on the rows that exist.
    [Fact]
    public async Task ASecondMigrateAddsNothingAndChangesNoSeededId()
    {
        var database = await postgres.CreateDatabaseAsync();
        var schema = await MigratedTenantAsync(database, Guid.NewGuid());
        var before = (await ModuleRowsAsync(database, schema), await PermissionRowsAsync(database, schema), await Db.ColumnAsync(database, PlanRows), await Db.ColumnAsync(database, PlanModuleRows));

        var again = await MigratorRun.ExecuteAsync(database);

        Assert.True(again.ExitCode == 0, again.Report);
        Assert.Equal(before.Item1, await ModuleRowsAsync(database, schema));
        Assert.Equal(before.Item2, await PermissionRowsAsync(database, schema));
        Assert.Equal(before.Item3, await Db.ColumnAsync(database, PlanRows));
        Assert.Equal(before.Item4, await Db.ColumnAsync(database, PlanModuleRows));
        Assert.Equal(ExpectedPermissionCodes.Length, before.Item2.Count);
    }

    // Breaks when seeding needs the migrator to hold the tenant role (the migrator login has ADMIN but not SET on it) or when
    // the rows are not visible to the application login acting as the tenant role.
    [Fact]
    public async Task SeededRowsAreReadableByTheApplicationLoginAfterSetRoleAndSeedingNeedsNoSetOnTheTenantRole()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        Assert.True((await MigratorRun.ExecuteAsync(logins.MigratorConnection, logins.RoleArgument)).ExitCode == 0);
        var tenant = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(logins.MigratorConnection, tenant);

        var run = await MigratorRun.ExecuteAsync(logins.MigratorConnection, logins.RoleArgument);

        Assert.True(run.ExitCode == 0, run.Report);
        var role = Assert.Single(await Db.ColumnAsync(
            logins.MigratorConnection,
            $"SELECT 't' || oid || '_{tenant:N}' FROM pg_database WHERE datname = current_database()"));
        Assert.Equal(["False"], await Db.ColumnAsync(logins.MigratorConnection, $"SELECT pg_has_role('{logins.Migrator}', '{role}', 'SET')"));
        await using var connection = await Db.OpenAsync(logins.ApplicationConnection);
        await using (var set = new NpgsqlCommand($"SET ROLE \"{role}\"", connection))
        {
            await set.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var schema = Schema(tenant);
        Assert.Equal(ExpectedModuleCodes.Length, await CountAsync(connection, $"SELECT count(*) FROM \"{schema}\".security_modules"));
        Assert.Equal(ExpectedPermissionCodes.Length, await CountAsync(connection, $"SELECT count(*) FROM \"{schema}\".security_permissions"));
    }

    private static async Task<long> CountAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // Test SQL with GUID-based names.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    // Breaks when migrate does not create the STANDARD and OPERATOR plans (even with no tenant), when OPERATOR does not
    // include every module of the catalogue, or when a rerun adds a plan or a plan-module row.
    [Fact]
    public async Task MigrateSeedsTheStandardAndOperatorPlansWithTheCatalogueModulesAndARerunAddsNothing()
    {
        var database = await postgres.CreateDatabaseAsync();

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.True(run.ExitCode == 0, run.Report);
        var plans = await Db.ColumnAsync(database, PlanRows);
        Assert.Equal(
            [$"{SeedIds.Plan("OPERATOR")}|OPERATOR", $"{SeedIds.Plan("STANDARD")}|STANDARD"],
            plans);
        var expectedModules = ExpectedModuleCodes.Select(c => SecurityModuleId.FromCode(c)).Order().ToArray();
        foreach (var plan in new[] { "OPERATOR", "STANDARD" })
        {
            var modules = await Db.ColumnAsync(
                database,
                "SELECT m.module_id FROM catalog.subscription_plan_modules m JOIN catalog.subscription_plans p ON p.id = m.plan_id "
                + $"WHERE p.code = '{plan}'");
            Assert.Equal(expectedModules, modules.Select(Guid.Parse).Order().ToArray());
        }

        var rows = await Db.ColumnAsync(database, PlanModuleRows);

        var again = await MigratorRun.ExecuteAsync(database);

        Assert.True(again.ExitCode == 0, again.Report);
        Assert.Equal(plans, await Db.ColumnAsync(database, PlanRows));
        Assert.Equal(rows, await Db.ColumnAsync(database, PlanModuleRows));
        Assert.Equal(2, await Db.CountAsync(database, "SELECT count(*) FROM catalog.subscription_plans WHERE code IN ('STANDARD', 'OPERATOR')"));
    }

    // Breaks when the standard plan includes the platform administration module, or the operator plan misses it.
    [Fact]
    public void PlanCompositionGivesTheOperatorEveryModuleAndTheStandardPlanEveryModuleExceptPlatform()
    {
        var catalogue = new PermissionCatalogue(["Access.Users.Read", "Billing.Invoices.Read", "Platform.Tenants.Read", "Platform.Tenants.Manage"]);

        var plans = PlanComposition.For(catalogue);

        Assert.Equal(
            ["Access", "Billing", "Platform"],
            Assert.Single(plans, p => p.Code == "OPERATOR").Modules);
        Assert.Equal(
            ["Access", "Billing"],
            Assert.Single(plans, p => p.Code == "STANDARD").Modules);
    }

    // Breaks when the standard plan drops a module just because no operator-only module exists.
    [Fact]
    public void PlanCompositionGivesBothPlansTheSameModulesWhenThereIsNoPlatformModule()
    {
        var plans = PlanComposition.For(new PermissionCatalogue(["Access.Users.Read", "Access.Roles.Read"]));

        Assert.Equal(["Access"], Assert.Single(plans, p => p.Code == "OPERATOR").Modules);
        Assert.Equal(["Access"], Assert.Single(plans, p => p.Code == "STANDARD").Modules);
    }

    private static async Task<Guid> AddRoleAsync(string database, string schema, string name, Guid? roleId = null)
    {
        var id = roleId ?? Guid.NewGuid();
        await Db.ExecuteAsync(
            database,
            $"INSERT INTO \"{schema}\".roles (id, name, normalized_name, created_at) VALUES ('{id}', '{name}', '{name.ToUpperInvariant()}', now())");
        return id;
    }

    private static Task<List<string>> RoleCodesAsync(string database, string schema, Guid role) =>
        Db.ColumnAsync(
            database,
            $"SELECT p.code FROM \"{schema}\".role_permissions rp JOIN \"{schema}\".security_permissions p ON p.id = rp.permission_id "
            + $"WHERE rp.role_id = '{role}' ORDER BY p.code COLLATE \"C\"");

    // Breaks when migrate leaves an existing Administrator role with fewer permissions than the code declares (a permission
    // declared after the role was made), gives rights to a role of another name, or duplicates a grant on a rerun.
    [Fact]
    public async Task MigrateGivesAnExistingAdministratorRoleEveryPermissionOfTheCatalogueIncludingNewlyDeclaredOnes()
    {
        var database = await postgres.CreateDatabaseAsync();
        var tenant = Guid.NewGuid();
        var schema = await MigratedTenantAsync(database, tenant);
        var administrator = await AddRoleAsync(database, schema, "Administrator", SeedIds.AdministratorRole);
        var other = await AddRoleAsync(database, schema, "Auditor");
        var newest = ExpectedPermissionCodes[^1];
        await Db.ExecuteAsync(
            database,
            $"DELETE FROM \"{schema}\".role_permissions WHERE permission_id = '{SeedIds.Permission(newest)}'; "
            + $"DELETE FROM \"{schema}\".security_permissions WHERE id = '{SeedIds.Permission(newest)}'");

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.True(run.ExitCode == 0, run.Report);
        Assert.Equal(ExpectedPermissionCodes, await RoleCodesAsync(database, schema, administrator));
        Assert.Empty(await RoleCodesAsync(database, schema, other));

        var again = await MigratorRun.ExecuteAsync(database);

        Assert.True(again.ExitCode == 0, again.Report);
        Assert.Equal(ExpectedPermissionCodes, await RoleCodesAsync(database, schema, administrator));
    }

    // Breaks when the top-up is done in only the first tenant, or creates the role where none exists (only bootstrap does).
    [Fact]
    public async Task EveryTenantWithAnAdministratorRoleIsToppedUpAndATenantWithoutOneGainsNoRole()
    {
        var database = await postgres.CreateDatabaseAsync();
        var (withRole, withoutRole) = (Guid.NewGuid(), Guid.NewGuid());
        var schemaWith = await MigratedTenantAsync(database, withRole);
        await CatalogSeed.AddTenantAsync(database, withoutRole);
        var administrator = await AddRoleAsync(database, schemaWith, "Administrator", SeedIds.AdministratorRole);

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.True(run.ExitCode == 0, run.Report);
        Assert.Equal(ExpectedPermissionCodes, await RoleCodesAsync(database, schemaWith, administrator));
        Assert.Equal(0, await Db.CountAsync(database, $"SELECT count(*) FROM \"{Schema(withoutRole)}\".roles"));
    }
}
