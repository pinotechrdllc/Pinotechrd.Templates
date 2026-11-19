using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.BuildingBlocks.Web;
using Npgsql;

#pragma warning disable EF1002 // The SQL is written by the tests, with names made from GUIDs.

namespace MyApp.Api.Tests.Tenancy;

// A deployment with two logins: the migrator's (not a superuser; it can create roles and owns the database) and the API's
// (a plain login that only becomes tenant roles). Real PostgreSQL (Docker); every login and database made here is dropped.
[Collection(PostgresContainer.Name)]
public class TwoLoginTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void MigratorModules(IServiceCollection services, string applicationRole)
    {
        services.AddTenantDbContext<DocsContext>("Docs");
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator(options => options.ApplicationRole = applicationRole);
    }

    private static void ApplicationModules(IServiceCollection services)
    {
        services.AddTenantDbContext<DocsContext>("Docs");
        services.AddTenantDbContext<AccessContext>("Access");
    }

    // A database owned by the migrator login, which may create roles and nothing else special. The shared role is the
    // database's own (its name carries the database's oid), so no other database's migrator has made it.
    private async Task<(string Database, DedicatedLogin Migrator, DedicatedLogin Application)> DeployAsync()
    {
        var migrator = await DedicatedLogin.CreateAsync(postgres);
        var application = await DedicatedLogin.CreateAsync(postgres);
        var database = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()).Database!;
        migrator.Drops(database);
        application.Drops(database);
        await using var admin = TenantRig.StartOn(
            postgres.Connection(database).ConnectionString, new NpgsqlDataSourceBuilder(postgres.Connection(database).ConnectionString).Build(), true, _ => { });
        await admin.ExecuteAsync(
            $"ALTER ROLE {migrator.Name} CREATEROLE; ALTER DATABASE \"{database}\" OWNER TO {migrator.Name}");
        return (database, migrator, application);
    }

    private static string RowsOfMembership(TenantRig rig, Guid tenant, string login) =>
        "SELECT m.inherit_option || ',' || m.set_option FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid "
        + $"JOIN pg_roles u ON u.oid = m.member WHERE r.rolname = '{rig.RoleOf(tenant)}' AND u.rolname = '{login}'";

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

    // Breaks when the migrator needs to be a superuser, when the application login is not made a member of each tenant role
    // with SET and without INHERIT (Migrator:ApplicationRole names it), or when the second tenant fails after the first
    // (the shared role and its grants already exist).
    [Fact]
    public async Task AMigratorThatIsNotASuperuserMigratesTenantsAndMakesTheApplicationLoginAMemberWithSetAndWithoutInherit()
    {
        var (database, migratorLogin, applicationLogin) = await DeployAsync();
        await using var _m = migratorLogin;
        await using var _a = applicationLogin;
        await using var migrator = migratorLogin.RigOn(database, s => MigratorModules(s, applicationLogin.Name), maxPoolSize: 6);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());

        var results = await migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync([a, b], 2, Token);

        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
        Assert.False(await migrator.ScalarAsync<bool>($"SELECT rolsuper FROM pg_roles WHERE rolname = '{migratorLogin.Name}'"));
        foreach (var tenant in new[] { a, b })
        {
            Assert.Equal(["false,true"], await migrator.ColumnAsync(RowsOfMembership(migrator, tenant, applicationLogin.Name)));
            Assert.False(await migrator.ScalarAsync<bool>($"SELECT rolcanlogin FROM pg_roles WHERE rolname = '{migrator.RoleOf(tenant)}'"));
        }
    }

    // Breaks when the application login (a plain login with no right on any tenant schema of its own) cannot work as the
    // tenant's role: it reads and writes its own tenant, runs as the role on the tenant's schema, still uses citext, and
    // after the connection returns to the pool runs as itself again.
    [Fact]
    public async Task TheApplicationLoginWorksOnItsTenantAsTheTenantRoleAndGoesBackToItselfAfterwards()
    {
        var (database, migratorLogin, applicationLogin) = await DeployAsync();
        await using var _m = migratorLogin;
        await using var _a = applicationLogin;
        var tenant = Guid.NewGuid();
        await using (var migrator = migratorLogin.RigOn(database, s => MigratorModules(s, applicationLogin.Name), maxPoolSize: 4))
        {
            Assert.True((await migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token)).Succeeded);
        }

        await using var application = applicationLogin.RigOn(database, ApplicationModules, maxPoolSize: 1);
        await using (var scope = application.ScopeFor(tenant))
        {
            var docs = scope.ServiceProvider.GetRequiredService<DocsContext>();
            docs.Docs.Add(new DocItem { Title = "Hello" });
            await docs.SaveChangesAsync(Token);

            Assert.Single(await docs.Docs.Where(d => d.Title == "HELLO").ToListAsync(Token));
            Assert.Equal(application.RoleOf(tenant), await docs.Database.SqlQueryRaw<string>("SELECT current_user AS \"Value\"").SingleAsync(Token));
            Assert.Equal(TenantSchemaName.For(tenant), await docs.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(Token));
        }

        await using var plain = application.DataSource.CreateCommand("SELECT current_user, session_user");
        await using var reader = await plain.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(applicationLogin.Name, reader.GetString(0));
        Assert.Equal(applicationLogin.Name, reader.GetString(1));
    }

    // Breaks when the application login, as one tenant, reaches another tenant's tables or the extensions schema for writing:
    // PostgreSQL refuses each with 42501 (insufficient privilege).
    [Fact]
    public async Task AsTenantAOnTheApplicationLoginCannotReadTenantBsTablesNorCreateInExtensions()
    {
        var (database, migratorLogin, applicationLogin) = await DeployAsync();
        await using var _m = migratorLogin;
        await using var _a = applicationLogin;
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await using (var migrator = migratorLogin.RigOn(database, s => MigratorModules(s, applicationLogin.Name), maxPoolSize: 6))
        {
            var results = await migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync([a, b], 2, Token);
            Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
        }

        await using var application = applicationLogin.RigOn(database, ApplicationModules, maxPoolSize: 2);
        await using var scope = application.ScopeFor(a);
        var context = scope.ServiceProvider.GetRequiredService<DocsContext>();

        var read = await SqlStateOfAsync(() => context.Database
            .SqlQueryRaw<string>($"SELECT title AS \"Value\" FROM \"{TenantSchemaName.For(b)}\".docs_items").ToListAsync(Token));
        var create = await SqlStateOfAsync(() => context.Database.ExecuteSqlRawAsync("CREATE TABLE extensions.written_by_tenant (id integer)", Token));

        Assert.Equal(["42501", "42501"], [read, create]);
    }

    // Breaks when a login that is not a member of the tenant's role can read the tenant's schema (the rights would then not
    // come from the role): a login the migrator was not told about gets 42501 on the tenant's tables.
    [Fact]
    public async Task ALoginThatIsNotAMemberOfTheTenantRoleIsDeniedOnTheTenantsTables()
    {
        var (database, migratorLogin, applicationLogin) = await DeployAsync();
        await using var _m = migratorLogin;
        await using var _a = applicationLogin;
        await using var outsider = await DedicatedLogin.CreateAsync(postgres);
        var tenant = Guid.NewGuid();
        await using (var migrator = migratorLogin.RigOn(database, s => MigratorModules(s, applicationLogin.Name), maxPoolSize: 4))
        {
            Assert.True((await migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token)).Succeeded);
        }

        var connection = postgres.Connection(database);
        connection.Username = outsider.Name;
        connection.Password = outsider.Password;
        connection.Pooling = false;
        await using var open = new NpgsqlConnection(connection.ConnectionString);
        await open.OpenAsync(Token);
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM \"{TenantSchemaName.For(tenant)}\".docs_items", open);

        var state = await SqlStateOfAsync(() => command.ExecuteScalarAsync(Token));

        Assert.Equal("42501", state);
    }

    // Breaks when an application login that is not a member of its tenant role is accepted: the API refuses the connection
    // and says the login is not a member and where to set it, instead of running as the login.
    [Fact]
    public async Task AnApplicationLoginThatIsNotAMemberOfTheTenantRoleFailsTheOpenNamingTheSetting()
    {
        var (database, migratorLogin, applicationLogin) = await DeployAsync();
        await using var _m = migratorLogin;
        await using var _a = applicationLogin;
        var tenant = Guid.NewGuid();
        await using (var migrator = migratorLogin.RigOn(database, s => MigratorModules(s, migratorLogin.Name), maxPoolSize: 4))
        {
            Assert.True((await migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token)).Succeeded);
        }

        await using var application = applicationLogin.RigOn(database, ApplicationModules, maxPoolSize: 2);
        await using var scope = application.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));

        Assert.Contains("Migrator:ApplicationRole", error.Message, StringComparison.Ordinal);
    }

    // Breaks when the readiness probe needs more than the restricted login has (it runs with no tenant): the host answers
    // healthy on the application login's connection string.
    [Fact]
    public async Task TheReadinessProbePassesAsTheApplicationLogin()
    {
        var (database, migratorLogin, applicationLogin) = await DeployAsync();
        await using var _m = migratorLogin;
        await using var _a = applicationLogin;
        var connection = postgres.Connection(database);
        connection.Username = applicationLogin.Name;
        connection.Password = applicationLogin.Password;
        using var factory = new ApiHostFactory().WithDatabase(connection.ConnectionString);
        using var client = factory.CreateClient();
        await using (var warm = factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT 0 AS warm_up"))
        {
            await warm.ExecuteScalarAsync(Token);
        }

        using var ready = await client.GetAsync(HealthRegistration.ReadyPath, Token);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }
}
