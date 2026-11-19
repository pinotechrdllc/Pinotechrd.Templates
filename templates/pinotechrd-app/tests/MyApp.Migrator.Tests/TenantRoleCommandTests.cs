using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Provisioning;
using MyApp.Migrator.Tests.Support;
using Npgsql;

namespace MyApp.Migrator.Tests;

// The database role of each tenant, through the migrator command (migrate and provision) on a real PostgreSQL server.
[Collection(PostgresContainer.Name)]
public class TenantRoleCommandTests(PostgresFixture postgres)
{
    private const string ApplicationRoleKey = "Migrator:ApplicationRole";

    // The tenant's role as a SQL expression in the database the query runs in: t<database oid>_<tenant id as 32 hex digits>.
    private static string RoleOf(Guid tenant) =>
        $"(SELECT 't' || oid || '_{tenant:N}' FROM pg_database WHERE datname = current_database())";

    private static string RoleFacts(Guid tenant, string schema) =>
        $"SELECT r.rolcanlogin || ',' || r.rolsuper || ',' || has_schema_privilege(r.rolname, '{schema}', 'USAGE') || ',' "
        + $"|| has_schema_privilege(r.rolname, '{schema}', 'CREATE') || ',' "
        + $"|| (has_table_privilege(r.rolname, '\"{schema}\".users', 'SELECT') AND has_table_privilege(r.rolname, '\"{schema}\".users', 'INSERT') "
        + $"AND has_table_privilege(r.rolname, '\"{schema}\".users', 'UPDATE') AND has_table_privilege(r.rolname, '\"{schema}\".users', 'DELETE')) "
        + $"FROM pg_roles r WHERE r.rolname = {RoleOf(tenant)}";

    private static async Task AdminAsync(PostgresFixture postgres, string sql)
    {
        var admin = postgres.Connection("postgres");
        admin.Pooling = false;
        await Db.ExecuteAsync(admin.ConnectionString, sql);
    }

    // Breaks when the migrate command leaves a catalog tenant without its role, or with a role that can log in, is a
    // superuser, cannot use the schema, or may create in it.
    [Fact]
    public async Task MigrateCreatesEachCatalogTenantsNoLoginRoleWithUsageOnTheSchemaAndDataRightsButNoCreate()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var tenant = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(database, tenant);

        var run = await MigratorRun.ExecuteAsync(database);

        Assert.True(run.ExitCode == 0, run.Report);
        Assert.Equal(["false,false,true,false,true"], await Db.ColumnAsync(database, RoleFacts(tenant, TenantSchemaName.For(tenant))));
    }

    // Breaks when provisioning a tenant does not give it a role (the API would refuse every connection of the new tenant).
    [Fact]
    public async Task ProvisioningATenantCreatesItsRole()
    {
        var (database, subscription) = await ProvisionSupport.ArrangeAsync(postgres);

        var run = await ProvisionSupport.RunAsync(database, "acme", "Acme", subscription);

        Assert.True(run.ExitCode == 0, run.Report);
        var row = Assert.Single(await ProvisionSupport.RowsAsync(database));
        Assert.Equal(["false,false,true,false,true"], await Db.ColumnAsync(database, RoleFacts(row.Id, TenantSchemaName.For(row.Id))));
    }

    // Breaks when running the command twice changes the role's rights or memberships, or fails the second time.
    [Fact]
    public async Task RunningMigrateTwiceChangesNothingAndExitsZero()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var tenant = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(database, tenant);
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var state = $"SELECT string_agg(x, ';' ORDER BY x) FROM (SELECT relname || '=' || coalesce(relacl::text, '') AS x FROM pg_class "
            + $"WHERE relnamespace = '{TenantSchemaName.For(tenant)}'::regnamespace UNION ALL "
            + "SELECT r.rolname || '>' || u.rolname || m.inherit_option || m.set_option FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid "
            + $"JOIN pg_roles u ON u.oid = m.member WHERE r.rolname = {RoleOf(tenant)}) s";
        var before = await Db.ColumnAsync(database, state);

        var again = await MigratorRun.ExecuteAsync(database);

        Assert.True(again.ExitCode == 0, again.Report);
        Assert.Equal(before, await Db.ColumnAsync(database, state));
    }

    // Breaks when Migrator:ApplicationRole is not read from the command line settings: the named login must become a member
    // of the tenant's role with SET and without INHERIT, and the run must succeed.
    [Fact]
    public async Task MigratorApplicationRoleMakesThatLoginAMemberOfTheTenantRoleWithSetAndWithoutInherit()
    {
        var login = "r_" + Guid.NewGuid().ToString("N")[..16];
        await AdminAsync(postgres, $"CREATE ROLE {login} LOGIN");
        var database = await postgres.CreateDatabaseAsync();
        try
        {
            Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
            var tenant = Guid.NewGuid();
            await CatalogSeed.AddTenantAsync(database, tenant);

            var run = await MigratorRun.ExecuteAsync(database, $"--{ApplicationRoleKey}={login}");

            Assert.True(run.ExitCode == 0, run.Report);
            Assert.Equal(
                ["false,true"],
                await Db.ColumnAsync(
                    database,
                    "SELECT m.inherit_option || ',' || m.set_option FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid "
                    + $"JOIN pg_roles u ON u.oid = m.member WHERE r.rolname = {RoleOf(tenant)} AND u.rolname = '{login}'"));
        }
        finally
        {
            var name = new NpgsqlConnectionStringBuilder(database).Database;
            await AdminAsync(postgres, $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)");
            await AdminAsync(postgres, $"DROP OWNED BY {login}");
            await AdminAsync(postgres, $"DROP ROLE IF EXISTS {login}");
        }
    }

    // Breaks when an application role that does not exist is accepted silently (the tenants would then be unreachable): the
    // run exits 1 and the report names the setting (Migrator:ApplicationRole) and says it does not exist.
    [Fact]
    public async Task AnApplicationRoleThatDoesNotExistFailsTheRunNamingTheSetting()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        await CatalogSeed.AddTenantAsync(database, Guid.NewGuid());
        var missing = "no_such_" + Guid.NewGuid().ToString("N")[..12];

        var run = await MigratorRun.ExecuteAsync(database, $"--{ApplicationRoleKey}={missing}");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains("ApplicationRole", run.Report, StringComparison.Ordinal);
        Assert.Contains("does not exist", run.Report, StringComparison.Ordinal);
    }

    // Breaks when the migrator and provision need a superuser: with a migrator login that only has CREATEROLE and owns the
    // database, and an application login named in Migrator:ApplicationRole, both commands exit 0 and the application login
    // becomes a member of the provisioned tenant's role with SET and without INHERIT.
    [Fact]
    public async Task MigrateAndProvisionSucceedWithAMigratorLoginThatIsNotASuperuserAndASeparateApplicationLogin()
    {
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var (migrator, application, password) = ("m_" + suffix, "a_" + suffix, "Pw-" + Guid.NewGuid().ToString("N"));
        var superuser = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var database = superuser.Database!;
        await AdminAsync(postgres, $"CREATE ROLE {migrator} LOGIN CREATEROLE PASSWORD '{password}'; CREATE ROLE {application} LOGIN PASSWORD '{password}'");
        try
        {
            await AdminAsync(postgres, $"ALTER DATABASE \"{database}\" OWNER TO {migrator}");
            var asMigrator = new NpgsqlConnectionStringBuilder(superuser.ConnectionString) { Username = migrator, Password = password }.ConnectionString;
            var roleArgument = $"--{ApplicationRoleKey}={application}";
            Assert.True((await MigratorRun.ExecuteAsync(asMigrator, roleArgument)).ExitCode == 0);
            var subscription = await CatalogSeed.AddSubscriptionAsync(asMigrator);
            var existing = Guid.NewGuid();
            await CatalogSeed.AddTenantAsync(asMigrator, existing);

            var migrated = await MigratorRun.ExecuteAsync(asMigrator, roleArgument);
            var provisioned = await MigratorRun.ExecuteRawAsync(
                ProvisionSupport.Verb, $"--{MigratorRun.ConnectionKey}={asMigrator}", roleArgument,
                "--Provision:Slug=acme", "--Provision:Name=Acme", $"--Provision:SubscriptionId={subscription:D}");

            Assert.True(migrated.ExitCode == 0, migrated.Report);
            Assert.True(provisioned.ExitCode == 0, provisioned.Report);
            var row = Assert.Single(await ProvisionSupport.RowsAsync(asMigrator, "acme"));
            foreach (var tenant in new[] { existing, row.Id })
            {
                Assert.Equal(
                    ["false,true"],
                    await Db.ColumnAsync(
                        asMigrator,
                        "SELECT m.inherit_option || ',' || m.set_option FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid "
                        + $"JOIN pg_roles u ON u.oid = m.member WHERE r.rolname = {RoleOf(tenant)} AND u.rolname = '{application}'"));
            }

            Assert.Equal(["False"], await Db.ColumnAsync(asMigrator, $"SELECT rolsuper FROM pg_roles WHERE rolname = '{migrator}'"));
        }
        finally
        {
            await AdminAsync(postgres, $"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await AdminAsync(postgres, $"DROP OWNED BY {migrator}; DROP OWNED BY {application}");
            await AdminAsync(postgres, $"DROP ROLE IF EXISTS {application}; DROP ROLE IF EXISTS {migrator}");
        }
    }
}
