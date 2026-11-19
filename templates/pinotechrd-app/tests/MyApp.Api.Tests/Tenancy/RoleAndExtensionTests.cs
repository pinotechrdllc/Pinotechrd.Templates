using System.Data;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// Extensions and database roles: a role that cannot use the shared extensions schema, a migrator that grants it, a migrator
// role that cannot create schemas, extensions that need others, and one that cannot be moved. Needs Docker. Every login role
// and database made here is dropped at the end.
[Collection(PostgresContainer.Name)]
public class RoleAndExtensionTests(PostgresFixture postgres)
{
    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void AccessModule(IServiceCollection services)
    {
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator();
    }

    private static void DocsModule(IServiceCollection services)
    {
        services.AddTenantDbContext<DocsContext>("Docs");
        services.AddTenantMigrator();
    }

    // What the migrator does for its application role: the login may become the tenant's role (and only that, never inherit it).
    private static Task MakeMemberAsync(TenantRig admin, string login, Guid tenant) =>
        admin.ExecuteAsync($"GRANT \"{admin.RoleOf(tenant)}\" TO {login} WITH INHERIT FALSE, SET TRUE");

    private static TenantRig AsSuperuser(PostgresFixture postgres, TenantRig first, Action<IServiceCollection> register)
    {
        var connection = postgres.Connection(first.Database);
        connection.MaxPoolSize = 8;
        return TenantRig.StartOn(connection.ConnectionString, new NpgsqlDataSourceBuilder(connection.ConnectionString).Build(), ownsDataSource: true, register);
    }

    // Breaks when a role that cannot use the extensions schema gets a tenant connection (the extensions would be silently
    // invisible), when the message does not name the schema and the privilege, or when the migrator's ApplicationRole does not
    // grant it: after a migrator run the role opens and citext compares case-insensitively.
    [Fact]
    public async Task ARoleWithoutUsageOnExtensionsFailsAtOpenAndWorksOnceTheMigratorGrantsIt()
    {
        await using var login = await DedicatedLogin.CreateAsync(postgres);
        await using var admin = await TenantRig.StartAsync(postgres, maxPoolSize: 3, register: AccessModule);
        login.Drops(admin.Database);
        var first = Guid.NewGuid();
        await admin.ProvisionAsync(first);
        await MakeMemberAsync(admin, login.Name, first);
        await admin.ExecuteAsync($"CREATE SCHEMA IF NOT EXISTS extensions; REVOKE USAGE ON SCHEMA extensions FROM \"{admin.SharedRole}\"");
        await using (var asRole = login.RigOn(admin.Database, AccessModule, maxPoolSize: 3))
        {
            await using var scope = asRole.ScopeFor(first);
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));

            Assert.Contains("extensions", error.Message, StringComparison.Ordinal);
            Assert.Contains("USAGE", error.Message, StringComparison.Ordinal);
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }

        await using var migrator = AsSuperuser(postgres, admin, services =>
        {
            services.AddTenantDbContext<DocsContext>("Docs");
            services.AddTenantMigrator(options => options.ApplicationRole = login.Name);
        });
        var second = Guid.NewGuid();
        var result = await migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(second, Token);
        Assert.True(result.Succeeded, result.Error?.ToString());

        await using var granted = login.RigOn(admin.Database, services =>
        {
            services.AddTenantDbContext<DocsContext>("Docs");
            services.AddTenantDbContext<AccessContext>("Access");
        }, maxPoolSize: 3);
        await using var docsScope = granted.ScopeFor(second);
        var docs = docsScope.ServiceProvider.GetRequiredService<DocsContext>();
        docs.Docs.Add(new DocItem { Title = "Hello" });
        await docs.SaveChangesAsync(Token);
        Assert.Single(await docs.Docs.Where(d => d.Title == "HELLO").ToListAsync(Token));
        await using var firstScope = granted.ScopeFor(first);
        Assert.Equal(TenantSchemaName.For(first), await firstScope.ServiceProvider.GetRequiredService<AccessContext>().Database.SqlQueryRaw<string>(SchemaSql).SingleAsync(Token));
    }

    // Breaks when the migrator needs CREATE on the database even when the extensions schema and its extensions already exist:
    // a role that owns its tenant schema and cannot create schemas migrates a model that declares installed extensions.
    [Fact]
    public async Task ARoleWithoutCreateOnTheDatabaseMigratesWhenTheExtensionsAreAlreadyInstalled()
    {
        await using var login = await DedicatedLogin.CreateAsync(postgres);
        await using var admin = await TenantRig.StartAsync(postgres, maxPoolSize: 3, register: DocsModule);
        login.Drops(admin.Database);
        var tenant = Guid.NewGuid();
        await admin.ExecuteAsync(
            "CREATE SCHEMA extensions; CREATE EXTENSION citext SCHEMA extensions; CREATE EXTENSION \"uuid-ossp\" SCHEMA extensions; "
            + $"ALTER SCHEMA extensions OWNER TO {login.Name}; ALTER ROLE {login.Name} CREATEROLE; REVOKE CREATE ON DATABASE \"{admin.Database}\" FROM PUBLIC; "
            + $"REVOKE CREATE ON DATABASE \"{admin.Database}\" FROM {login.Name}; CREATE SCHEMA \"{TenantSchemaName.For(tenant)}\" AUTHORIZATION {login.Name}");
        await using var asRole = login.RigOn(admin.Database, DocsModule, maxPoolSize: 5);

        var result = await asRole.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        await using var scope = asRole.ScopeFor(tenant);
        var docs = scope.ServiceProvider.GetRequiredService<DocsContext>();
        docs.Docs.Add(new DocItem { Title = "Case" });
        await docs.SaveChangesAsync(Token);
        Assert.Single(await docs.Docs.Where(d => d.Title == "CASE").ToListAsync(Token));
    }

    // Breaks when an extension that needs another is not installed with its requirement in the shared schema: earthdistance
    // needs cube, and both must end up in extensions.
    [Fact]
    public async Task AnExtensionThatNeedsAnotherInstallsBothInTheExtensionsSchema()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<EarthContext>("Earth");
                services.AddTenantMigrator();
            });

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal(
            ["cube@extensions", "earthdistance@extensions"],
            await rig.ColumnAsync("SELECT extname || '@' || extnamespace::regnamespace::text FROM pg_extension WHERE extname IN ('cube', 'earthdistance') ORDER BY 1"));
    }

    // Breaks when the advice for an extension that sits in public and cannot be moved (xml2 is not relocatable) is to move it
    // (ALTER EXTENSION ... SET SCHEMA fails for it): the message must say to drop and create it again.
    [Fact]
    public async Task ANonRelocatableExtensionInPublicGetsTheRecreateAdviceNotAlterExtension()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<Xml2Context>("Xml");
                services.AddTenantMigrator();
            });
        await rig.ExecuteAsync("CREATE EXTENSION xml2 SCHEMA public");

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.False(result.Succeeded);
        var message = Assert.IsType<InvalidOperationException>(result.Error).Message;
        Assert.Contains("not relocatable", message, StringComparison.Ordinal);
        Assert.Contains("drop it and create it again", message, StringComparison.Ordinal);
        Assert.DoesNotContain("ALTER EXTENSION", message, StringComparison.Ordinal);
    }

    // Breaks when the lost-lock check counts any advisory lock of the lock session: a session that holds only an unrelated lock
    // has lost the tenant's, and the run must stop. The check is a private method of the migrator, called through reflection
    // with a session of the test, because a real run cannot lose its tenant lock and keep another one.
    [Fact]
    public async Task ASessionHoldingOnlyAnUnrelatedAdvisoryLockHasLostTheTenantLock()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2);
        var type = typeof(TenantSchemaName).Assembly.GetType(typeof(TenantSchemaName).Namespace + ".TenantMigrator", throwOnError: true)!;
        var check = type.GetMethod("EnsureLockHeldAsync", BindingFlags.Static | BindingFlags.NonPublic)!;
        var prefix = (string)type.GetField("TenantLockPrefix", BindingFlags.Static | BindingFlags.NonPublic)!.GetRawConstantValue()!;
        var key = prefix + TenantSchemaName.For(Guid.NewGuid());
        await using var session = await rig.OpenAdminAsync();
        await ExecuteAsync(session, "SELECT pg_advisory_lock(hashtextextended('an-unrelated-lock', 0))");

        var lost = await Assert.ThrowsAsync<InvalidOperationException>(() => (Task)check.Invoke(null, [session, key, Token])!);
        Assert.Contains("lock", lost.Message, StringComparison.OrdinalIgnoreCase);

        await ExecuteAsync(session, $"SELECT pg_advisory_lock(hashtextextended('{key}', 0))");
        await (Task)check.Invoke(null, [session, key, Token])!;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(Token);
    }
}
