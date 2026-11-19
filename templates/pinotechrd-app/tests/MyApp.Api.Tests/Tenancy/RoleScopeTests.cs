using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

#pragma warning disable EF1002 // The SQL is written by the tests, with names made from GUIDs.

namespace MyApp.Api.Tests.Tenancy;

// Tenant roles live in the whole cluster, so their names must be scoped to the database, and a role or login that could
// act beyond its tenant must be refused. Real PostgreSQL (Docker); every login and database made here is dropped.
[Collection(PostgresContainer.Name)]
public class RoleScopeTests(PostgresFixture postgres)
{
    private const string SharedTable = ExcludedAndMigratedContext.SharedSchema + ".shared_probe";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void MigratorModules(IServiceCollection services, string applicationRole)
    {
        services.AddTenantDbContext<DocsContext>("Docs");
        services.AddTenantDbContext<ExcludedAndMigratedContext>("Mixed");
        services.AddTenantMigrator(options => options.ApplicationRole = applicationRole);
    }

    private static void ApplicationModules(IServiceCollection services)
    {
        services.AddTenantDbContext<DocsContext>("Docs");
        services.AddTenantDbContext<ExcludedAndMigratedContext>("Mixed");
    }

    private sealed record Deployment(string Database, DedicatedLogin Migrator, DedicatedLogin Application) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Application.DisposeAsync();
            await Migrator.DisposeAsync();
        }
    }

    // A database with a shared table, owned by its own migrator login (CREATEROLE, not a superuser), and its own plain
    // application login. Nothing is done about the cluster's other databases or roles.
    private async Task<Deployment> DeployAsync()
    {
        var migrator = await DedicatedLogin.CreateAsync(postgres);
        var application = await DedicatedLogin.CreateAsync(postgres);
        var database = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()).Database!;
        migrator.Drops(database);
        application.Drops(database);
        await AdminAsync(
            database,
            $"ALTER ROLE {migrator.Name} CREATEROLE; ALTER DATABASE \"{database}\" OWNER TO {migrator.Name}; "
            + $"CREATE SCHEMA {ExcludedAndMigratedContext.SharedSchema}; CREATE TABLE {SharedTable}(id uuid PRIMARY KEY); "
            + $"INSERT INTO {SharedTable} VALUES (gen_random_uuid()); ALTER SCHEMA {ExcludedAndMigratedContext.SharedSchema} OWNER TO {migrator.Name}; "
            + $"ALTER TABLE {SharedTable} OWNER TO {migrator.Name}");
        return new Deployment(database, migrator, application);
    }

    private string RoleOf(Deployment deployment, Guid tenant) => TenantRoles.For(postgres.Connection(deployment.Database).ConnectionString, tenant);

    private static async Task MigrateAsync(Deployment deployment, params Guid[] tenants)
    {
        await using var rig = deployment.Migrator.RigOn(deployment.Database, s => MigratorModules(s, deployment.Application.Name), maxPoolSize: 6);
        var results = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenants, 2, Token);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
    }

    private async Task AdminAsync(string database, string sql)
    {
        var admin = postgres.Connection(database);
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Token);
#pragma warning disable CA2100 // The SQL is written by the tests.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<T> ScalarAsync<T>(string database, string sql)
    {
        var admin = postgres.Connection(database);
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Token);
#pragma warning disable CA2100 // The SQL is written by the tests.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    private static async Task<string> SqlStateOfAsync(Func<Task> work)
    {
        var error = await Assert.ThrowsAnyAsync<Exception>(work);
        for (var current = error; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresError)
            {
                return postgresError.SqlState;
            }
        }

        throw new Xunit.Sdk.XunitException("No PostgreSQL error in: " + error);
    }

    private async Task<NpgsqlConnection> OpenAsLoginAsync(string database, DedicatedLogin login)
    {
        var connection = postgres.Connection(database);
        connection.Username = login.Name;
        connection.Password = login.Password;
        connection.Pooling = false;
        var open = new NpgsqlConnection(connection.ConnectionString);
        await open.OpenAsync(Token);
        return open;
    }

    private static async Task<string> ExecuteScalarTextAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // The SQL is written by the tests.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return Convert.ToString(await command.ExecuteScalarAsync(Token), System.Globalization.CultureInfo.InvariantCulture) ?? "null";
    }

    // Breaks when the shared role's name is not scoped to the database: the second database's migrator (a CREATEROLE login
    // that did not make the first one's shared role) would find a role it cannot administer and fail with 42501.
    [Fact]
    public async Task TwoDatabasesOfOneClusterMigrateEachWithItsOwnMigratorAndApplicationLogin()
    {
        await using var first = await DeployAsync();
        await using var second = await DeployAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        await MigrateAsync(first, a);
        await MigrateAsync(second, b);

        foreach (var (deployment, tenant) in new[] { (first, a), (second, b) })
        {
            await using var application = deployment.Application.RigOn(deployment.Database, ApplicationModules, maxPoolSize: 2);
            await using var scope = application.ScopeFor(tenant);
            var docs = scope.ServiceProvider.GetRequiredService<DocsContext>();
            docs.Docs.Add(new DocItem { Title = "Hello" });
            await docs.SaveChangesAsync(Token);
            Assert.Single(await docs.Docs.ToListAsync(Token));
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<ExcludedAndMigratedContext>().Set<SharedProbe>().CountAsync(Token));
        }
    }

    // Breaks when the tenant role of one database can use another database's objects: the application login of the first
    // database, once it is the first database's tenant role, is refused (42501) on the second database's shared table and
    // on its tenants' tables, while the second database's own login, as its own tenant role, reads them.
    [Fact]
    public async Task ATenantRoleOfOneDatabaseIsDeniedTheSharedAndTenantObjectsOfAnotherDatabase()
    {
        await using var first = await DeployAsync();
        await using var second = await DeployAsync();
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await MigrateAsync(first, a);
        await MigrateAsync(second, b);
        var foreignTable = $"\"{TenantSchemaName.For(b)}\".\"{ExcludedAndMigratedContext.Table}\"";

        await using var own = await OpenAsLoginAsync(second.Database, second.Application);
        _ = await ExecuteScalarTextAsync(own, $"SET ROLE \"{RoleOf(second, b)}\"");
        Assert.Equal("1", await ExecuteScalarTextAsync(own, $"SELECT count(*) FROM {SharedTable}"));

        await using var foreign = await OpenAsLoginAsync(second.Database, first.Application);
        _ = await ExecuteScalarTextAsync(foreign, $"SET ROLE \"{RoleOf(first, a)}\"");
        var shared = await SqlStateOfAsync(() => ExecuteScalarTextAsync(foreign, $"SELECT count(*) FROM {SharedTable}"));
        var tenant = await SqlStateOfAsync(() => ExecuteScalarTextAsync(foreign, $"SELECT count(*) FROM {foreignTable}"));

        Assert.Equal(["42501", "42501"], [shared, tenant]);
    }

    // Breaks when the guard only checks that the login can set the tenant role: a login that also INHERITs the role (a second
    // membership row, granted by someone else) would use the role's rights outside a tenant connection.
    [Fact]
    public async Task AnApplicationLoginThatInheritsTheTenantRoleIsRefusedWithAClearMessage()
    {
        await using var deployment = await DeployAsync();
        var tenant = Guid.NewGuid();
        await MigrateAsync(deployment, tenant);
        await AdminAsync(deployment.Database, $"GRANT \"{RoleOf(deployment, tenant)}\" TO {deployment.Application.Name} WITH INHERIT TRUE GRANTED BY CURRENT_USER");
        await using var application = deployment.Application.RigOn(deployment.Database, ApplicationModules, maxPoolSize: 2);
        await using var scope = application.ScopeFor(tenant);
        var docs = scope.ServiceProvider.GetRequiredService<DocsContext>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => docs.Docs.ToListAsync(Token));

        Assert.Contains("inherit", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    // Breaks when a login that is a superuser or bypasses row security is accepted by default (it would ignore the tenant
    // role's limits), or when the option that allows it is not honoured.
    [Theory]
    [InlineData("SUPERUSER")]
    [InlineData("BYPASSRLS")]
    public async Task APrivilegedApplicationLoginIsRefusedByDefaultAndAcceptedWhenTheOptionIsTrue(string attribute)
    {
        await using var deployment = await DeployAsync();
        var tenant = Guid.NewGuid();
        await MigrateAsync(deployment, tenant);
        await AdminAsync(deployment.Database, $"ALTER ROLE {deployment.Application.Name} {attribute}");

        await using (var refused = deployment.Application.RigOn(deployment.Database, ApplicationModules, maxPoolSize: 2))
        await using (var scope = refused.ScopeFor(tenant))
        {
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => scope.ServiceProvider.GetRequiredService<DocsContext>().Docs.ToListAsync(Token));
            Assert.Contains(TenancyOptions.AllowPrivilegedLoginKey, error.Message, StringComparison.Ordinal);
        }

        await using var allowed = deployment.Application.RigOn(
            deployment.Database,
            s =>
            {
                ApplicationModules(s);
                s.Configure<TenancyOptions>(o => o.AllowPrivilegedLogin = true);
            },
            maxPoolSize: 2);
        await using var allowedScope = allowed.ScopeFor(tenant);
        var docs = allowedScope.ServiceProvider.GetRequiredService<DocsContext>();
        Assert.Empty(await docs.Docs.ToListAsync(Token));
        Assert.Equal(allowed.RoleOf(tenant), await docs.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync(Token));
    }

    // Breaks when a pre-existing role that can log in or bypass row security is adopted as the tenant's role: the migrator
    // must fail naming the role problem and leave the role and the database as they were.
    [Theory]
    [InlineData("LOGIN PASSWORD 'x'", "LOGIN")]
    [InlineData("NOLOGIN BYPASSRLS", "BYPASSRLS")]
    [InlineData("NOLOGIN CREATEROLE", "CREATEROLE")]
    public async Task APreExistingTenantRoleWithAPrivilegedAttributeFailsTheMigrationAndIsNotAdopted(string attributes, string named)
    {
        var tenant = Guid.NewGuid();
        var database = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()).Database!;
        await using var drop = new DatabaseDrop(postgres, database);
        var connection = postgres.Connection(database);
        connection.MaxPoolSize = 6;
        await using var rig = TenantRig.StartOn(
            connection.ConnectionString,
            new NpgsqlDataSourceBuilder(connection.ConnectionString).Build(),
            ownsDataSource: true,
            s => { s.AddTenantDbContext<AccessContext>("Access"); s.AddTenantMigrator(); });
        var role = TenantRoles.For(connection.ConnectionString, tenant);
        await AdminAsync(database, $"CREATE ROLE \"{role}\" {attributes}");
        try
        {
            var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

            Assert.False(result.Succeeded);
            Assert.Contains(named, result.Error!.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(await rig.SchemaExistsAsync(TenantSchemaName.For(tenant)));
            Assert.Equal(0L, await rig.ScalarAsync<long>(
                $"SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.member WHERE r.rolname = '{role}'"));
            Assert.Equal(1L, await rig.ScalarAsync<long>(
                $"SELECT count(*) FROM pg_roles WHERE rolname = '{role}' AND (rolcanlogin OR rolbypassrls OR rolcreaterole)"));
        }
        finally
        {
            await AdminAsync(database, $"DROP ROLE IF EXISTS \"{role}\"");
        }
    }

    // Breaks when pg_temp comes before the tenant's schema in the search_path: a temporary table named like a tenant table
    // would then answer the next EF query instead of the real one.
    [Fact]
    public async Task ATemporaryTableNamedLikeATenantTableDoesNotShadowTheRealTable()
    {
        await using var deployment = await DeployAsync();
        var tenant = Guid.NewGuid();
        await MigrateAsync(deployment, tenant);
        await using var application = deployment.Application.RigOn(deployment.Database, ApplicationModules, maxPoolSize: 1);
        await using var scope = application.ScopeFor(tenant);
        var docs = scope.ServiceProvider.GetRequiredService<DocsContext>();
        docs.Docs.Add(new DocItem { Title = "REAL" });
        await docs.SaveChangesAsync(Token);

        await docs.Database.OpenTenantConnectionAsync(Token);
        var raw = (NpgsqlConnection)docs.Database.GetDbConnection();
        _ = await ExecuteScalarTextAsync(
            raw, "CREATE TEMP TABLE docs_items(id uuid, title text); INSERT INTO pg_temp.docs_items VALUES (gen_random_uuid(), 'TEMP'); SELECT 'made'");
        var titles = await docs.Docs.Select(d => d.Title).ToListAsync(Token);

        Assert.Equal(["REAL"], titles);
    }
}
