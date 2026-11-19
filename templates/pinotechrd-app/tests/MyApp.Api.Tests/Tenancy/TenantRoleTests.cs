using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

#pragma warning disable EF1002 // The SQL is written by the tests, with names made from GUIDs.

namespace MyApp.Api.Tests.Tenancy;

// The role of a tenant, as the migrator makes it and as a tenant connection uses it: what the role may do, what the login
// is allowed to do with it, what a connection reports, how it is reset, and what happens when it is missing. Real
// PostgreSQL (Docker). The login of these tests is the fixture's superuser: a connection that assumes the tenant role is
// restricted like any other role, so the checks are real.
[Collection(PostgresContainer.Name)]
public class TenantRoleTests(PostgresFixture postgres)
{
    private const string UserSql = "SELECT current_user AS \"Value\"";

    private const string SessionUserSql = "SELECT session_user AS \"Value\"";

    private const string SchemaSql = "SELECT current_schema() AS \"Value\"";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static void DocsAndAccess(IServiceCollection services)
    {
        services.AddTenantDbContext<DocsContext>("Docs");
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator();
    }

    private static async Task<string> ScalarAsync(DbContext context, string sql) =>
        await context.Database.SqlQueryRaw<string>(sql).SingleAsync(Token);

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

    // Breaks when the migrator does not create the role, makes it a login or a superuser, or forgets the schema (USAGE) or
    // the grants of a table of the schema (history tables included): every table of the tenant allows the four data
    // changes, and the role has no CREATE on the schema.
    [Fact]
    public async Task AfterAMigrationTheTenantRoleExistsWithoutLoginAndMayUseItsSchemaAndChangeEveryTableButNotCreate()
    {
        await using var rig = await TenantRig.StartAsync(postgres, register: DocsAndAccess);
        var tenant = Guid.NewGuid();
        var (role, schema) = (rig.RoleOf(tenant), TenantSchemaName.For(tenant));

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Matches($"^t[1-9][0-9]*_{tenant:N}$", role);
        Assert.True(await rig.ScalarAsync<bool>($"SELECT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{role}')"));
        Assert.False(await rig.ScalarAsync<bool>($"SELECT rolcanlogin FROM pg_roles WHERE rolname = '{role}'"));
        Assert.False(await rig.ScalarAsync<bool>($"SELECT rolsuper FROM pg_roles WHERE rolname = '{role}'"));
        Assert.True(await rig.ScalarAsync<bool>($"SELECT has_schema_privilege('{role}', '{schema}', 'USAGE')"));
        Assert.False(await rig.ScalarAsync<bool>($"SELECT has_schema_privilege('{role}', '{schema}', 'CREATE')"));
        var tables = await rig.ColumnAsync($"SELECT table_name FROM information_schema.tables WHERE table_schema = '{schema}' ORDER BY 1");
        Assert.Contains("access_items", tables);
        Assert.Contains("docs_items", tables);
        Assert.Contains("__ef_migrations_access", tables);
        foreach (var table in tables)
        {
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
            {
                Assert.True(
                    await rig.ScalarAsync<bool>($"SELECT has_table_privilege('{role}', '\"{schema}\".\"{table}\"', '{privilege}')"),
                    $"{privilege} on {table}");
            }
        }
    }

    // Breaks when the grants cover only the tables that exist at the first run: a table added by a later migration of a new
    // module, and a table created afterwards by the migrator's login itself (default privileges), both allow data changes.
    [Fact]
    public async Task ATableAddedLaterByAMigrationOrByTheMigratorLoginIsUsableByTheTenantRole()
    {
        await using var rig = await TenantRig.StartAsync(postgres, register: services =>
        {
            services.AddTenantDbContext<AccessContext>("Access");
            services.AddTenantMigrator();
        });
        var tenant = Guid.NewGuid();
        var (role, schema) = (rig.RoleOf(tenant), TenantSchemaName.For(tenant));
        Assert.True((await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token)).Succeeded);
        await rig.ExecuteAsync($"CREATE TABLE \"{schema}\".created_by_login (id integer)");
        var connection = postgres.Connection(rig.Database);
        connection.MaxPoolSize = 8;
        await using var later = TenantRig.StartOn(
            connection.ConnectionString, new NpgsqlDataSourceBuilder(connection.ConnectionString).Build(), ownsDataSource: true, services =>
            {
                services.AddTenantDbContext<AccessContext>("Access");
                services.AddTenantDbContext<BillingContext>("Billing");
                services.AddTenantMigrator();
            });

        var result = await later.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        foreach (var table in new[] { "billing_items", "created_by_login" })
        {
            foreach (var privilege in new[] { "SELECT", "INSERT", "UPDATE", "DELETE" })
            {
                Assert.True(await rig.ScalarAsync<bool>($"SELECT has_table_privilege('{role}', '\"{schema}\".\"{table}\"', '{privilege}')"), $"{privilege} on {table}");
            }
        }

        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        context.Items.Add(new FixtureItem { Name = "written-as-role" });
        await context.SaveChangesAsync(Token);
    }

    // Breaks when the migrator's own login is not made a member of the tenant role with INHERIT FALSE and SET TRUE (a login
    // that inherits the role's rights would make a restricted role pointless; one that cannot SET ROLE could not use it).
    [Fact]
    public async Task TheMigratorLoginIsAMemberOfTheTenantRoleWithSetAndWithoutInherit()
    {
        await using var rig = await TenantRig.StartAsync(postgres);
        var tenant = Guid.NewGuid();
        Assert.True((await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token)).Succeeded);

        var rows = await rig.ColumnAsync(
            "SELECT m.inherit_option || ',' || m.set_option FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid "
            + $"JOIN pg_roles u ON u.oid = m.member WHERE r.rolname = '{rig.RoleOf(tenant)}' AND u.rolname = '{PostgresFixture.Username}'");

        Assert.Equal(["false,true"], rows);
    }

    // Breaks when a tenant connection can read or write another tenant's tables (the role has no rights there), or write to
    // the shared extensions schema, or create a table in its own schema.
    [Fact]
    public async Task FromTenantAAConnectionCannotTouchTenantBsTablesCreateInExtensionsOrCreateInItsOwnSchema()
    {
        await using var rig = await TenantRig.StartAsync(postgres, register: DocsAndAccess);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        await rig.ExecuteAsync($"INSERT INTO \"{TenantSchemaName.For(b)}\".access_items (id, name) VALUES (gen_random_uuid(), 'b-secret')");
        await using var scope = rig.ScopeFor(a);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
        Assert.Empty(await context.Items.ToListAsync(Token));

        var read = await SqlStateOfAsync(() => context.Database
            .SqlQueryRaw<string>($"SELECT name AS \"Value\" FROM \"{TenantSchemaName.For(b)}\".access_items").ToListAsync(Token));
        var write = await SqlStateOfAsync(() => context.Database
            .ExecuteSqlRawAsync($"INSERT INTO \"{TenantSchemaName.For(b)}\".access_items (id, name) VALUES (gen_random_uuid(), 'x')", Token));
        var shared = await SqlStateOfAsync(() => context.Database.ExecuteSqlRawAsync("CREATE TABLE extensions.written_by_tenant (id integer)", Token));
        var own = await SqlStateOfAsync(() => context.Database.ExecuteSqlRawAsync("CREATE TABLE created_by_tenant (id integer)", Token));

        Assert.Equal(["42501", "42501", "42501", "42501"], [read, write, shared, own]);
        Assert.Equal(["b-secret"], await rig.ColumnAsync($"SELECT name FROM \"{TenantSchemaName.For(b)}\".access_items"));
        Assert.Equal(0, await rig.ScalarAsync<long>("SELECT count(*) FROM pg_class WHERE relname IN ('written_by_tenant', 'created_by_tenant')"));
    }

    // Breaks when a tenant connection does not run as its own role (current_user) on the tenant's schema (current_schema), or
    // when the role cannot use the shared extensions: citext still compares case-insensitively and uuid-ossp still answers.
    [Fact]
    public async Task ATenantConnectionRunsAsItsOwnRoleOnItsSchemaAndStillUsesTheSharedExtensions()
    {
        await using var rig = await TenantRig.StartAsync(postgres, register: DocsAndAccess);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);

        foreach (var tenant in new[] { a, b })
        {
            await using var scope = rig.ScopeFor(tenant);
            var docs = scope.ServiceProvider.GetRequiredService<DocsContext>();
            docs.Docs.Add(new DocItem { Title = "Hello" });
            await docs.SaveChangesAsync(Token);

            Assert.Equal(rig.RoleOf(tenant), await ScalarAsync(docs, UserSql));
            Assert.Equal(TenantSchemaName.For(tenant), await ScalarAsync(docs, SchemaSql));
            Assert.Equal(PostgresFixture.Username, await ScalarAsync(docs, SessionUserSql));
            Assert.Single(await docs.Docs.Where(d => d.Title == "HELLO").ToListAsync(Token));
            Assert.NotEqual(Guid.Empty, await docs.Database.SqlQueryRaw<Guid>("SELECT uuid_generate_v4() AS \"Value\"").SingleAsync(Token));
        }
    }

    // Breaks when the role of the connection is the previous tenant's after the connection changed hands (every open of one
    // physical connection must bind its own tenant's role), on a pool of one connection.
    [Fact]
    public async Task OneConnectionServingTwoTenantsOneAfterTheOtherRunsAsEachTenantsRole()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2, register: DocsAndAccess);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);
        var seen = new List<string>();

        foreach (var tenant in new[] { a, b, a })
        {
            await using var scope = rig.ScopeFor(tenant);
            seen.Add(await ScalarAsync(scope.ServiceProvider.GetRequiredService<AccessContext>(), UserSql));
        }

        Assert.Equal([rig.RoleOf(a), rig.RoleOf(b), rig.RoleOf(a)], seen);
    }

    // Breaks when the role assumed by a tenant connection survives its return to the pool: the next connection without a
    // tenant (the host's own data source) would run as the tenant, and so would the readiness probe. The pool has one
    // connection, so the next one is the same physical connection.
    [Fact]
    public async Task AConnectionReturnedToThePoolLosesTheTenantRole()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 1);
        var tenant = Guid.NewGuid();
        await rig.ProvisionAsync(tenant);

        string during;
        await using (var scope = rig.ScopeFor(tenant))
        {
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
            during = await ScalarAsync(context, UserSql);
            Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        }

        await using var plain = rig.DataSource.CreateCommand("SELECT current_user, session_user");
        await using var reader = await plain.ExecuteReaderAsync(Token);
        Assert.True(await reader.ReadAsync(Token));
        Assert.Equal(rig.RoleOf(tenant), during);
        Assert.Equal(PostgresFixture.Username, reader.GetString(1));
        Assert.Equal(reader.GetString(1), reader.GetString(0));
    }

    // Breaks when a schema without its role (a tenant that exists but was not migrated by this version) opens: the open must
    // fail with a message that says to run the migrator, close the connection, and work once the migrator has run.
    [Fact]
    public async Task ASchemaWithoutItsRoleFailsTheOpenSayingToRunTheMigratorAndWorksAfterTheMigratorRan()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 2, register: DocsAndAccess);
        var tenant = Guid.NewGuid();
        await rig.ExecuteAsync($"CREATE SCHEMA \"{TenantSchemaName.For(tenant)}\"");
        await using var scope = rig.ScopeFor(tenant);
        var context = scope.ServiceProvider.GetRequiredService<AccessContext>();

        var asynchronous = await Assert.ThrowsAsync<InvalidOperationException>(() => context.Items.ToListAsync(Token));
        var synchronous = Assert.Throws<InvalidOperationException>(() => context.Items.ToList());

        Assert.Contains("run the migrator", asynchronous.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("run the migrator", synchronous.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(PostgresFixture.Password, asynchronous.ToString(), StringComparison.Ordinal);
        Assert.Equal(ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.Equal(0, await rig.ScalarAsync<long>(
            $"SELECT count(*) FROM pg_stat_activity WHERE datname = '{rig.Database}' AND state = 'idle in transaction'"));

        Assert.True((await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token)).Succeeded);
        Assert.Empty(await context.Items.ToListAsync(Token));
        Assert.Equal(rig.RoleOf(tenant), await ScalarAsync(context, UserSql));
    }

    // Breaks when a second migration changes anything (a new role, other rights, other memberships, another result) or fails.
    [Fact]
    public async Task AMigrationRepeatedChangesNothingAndDoesNotFail()
    {
        await using var rig = await TenantRig.StartAsync(postgres, register: DocsAndAccess);
        var tenant = Guid.NewGuid();
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();
        Assert.True((await migrator.MigrateAsync(tenant, Token)).Succeeded);
        var before = await StateAsync(rig, tenant);

        var second = await migrator.MigrateAsync(tenant, Token);

        Assert.True(second.Succeeded, second.Error?.ToString());
        Assert.False(second.SchemaCreated);
        Assert.All(second.Modules, m => Assert.Empty(m.AppliedMigrations));
        Assert.NotEmpty(before);
        Assert.Equal(before, await StateAsync(rig, tenant));
    }

    // The role, its memberships and the rights on the tenant's schema and tables, as text.
    private static async Task<List<string>> StateAsync(TenantRig rig, Guid tenant)
    {
        var (role, schema) = (rig.RoleOf(tenant), TenantSchemaName.For(tenant));
        var rows = await rig.ColumnAsync($"SELECT oid::text || ':' || rolcanlogin || ':' || rolsuper FROM pg_roles WHERE rolname = '{role}'");
        rows.AddRange(await rig.ColumnAsync(
            "SELECT r.rolname || '>' || u.rolname || ':' || m.inherit_option || ':' || m.set_option || ':' || m.admin_option "
            + "FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid JOIN pg_roles u ON u.oid = m.member "
            + $"WHERE r.rolname = '{role}' OR u.rolname = '{role}' ORDER BY 1"));
        rows.AddRange(await rig.ColumnAsync($"SELECT nspacl::text FROM pg_namespace WHERE nspname = '{schema}'"));
        rows.AddRange(await rig.ColumnAsync(
            $"SELECT relname || '=' || coalesce(relacl::text, '') FROM pg_class WHERE relnamespace = '{schema}'::regnamespace ORDER BY 1"));
        rows.AddRange(await rig.ColumnAsync(
            $"SELECT defaclobjtype::text || '=' || coalesce(defaclacl::text, '') FROM pg_default_acl WHERE defaclnamespace = '{schema}'::regnamespace ORDER BY 1"));
        return rows;
    }

    // Breaks when parallel runs over many tenants collide on the role catalogs (the shared group role is granted to each
    // tenant role, and role rows are updated while others are created): no result may fail, whatever the degree. Three rounds
    // of ten tenants at the largest degree the pool allows.
    [Fact]
    public async Task TenTenantsAtTheMaximumDegreeOfParallelismAllGetTheirRolesWithoutACatalogCollision()
    {
        await using var rig = await TenantRig.StartAsync(postgres, maxPoolSize: 40, register: DocsAndAccess);
        var migrator = rig.Services.GetRequiredService<ITenantMigrator>();
        var all = new List<Guid>();

        for (var round = 0; round < 3; round++)
        {
            var tenants = Enumerable.Range(0, 10).Select(_ => Guid.NewGuid()).ToList();
            all.AddRange(tenants);

            var results = await migrator.MigrateAsync(tenants, 10, Token).WaitAsync(TimeSpan.FromMinutes(3), Token);

            Assert.Equal(10, results.Count);
            Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
            Assert.DoesNotContain(results, r => r.Error?.ToString().Contains("concurrently updated", StringComparison.OrdinalIgnoreCase) == true);
        }

        var names = string.Join(", ", all.Select(t => $"'{rig.RoleOf(t)}'"));
        Assert.Equal(30, await rig.ScalarAsync<long>($"SELECT count(*) FROM pg_roles WHERE rolname IN ({names}) AND NOT rolcanlogin"));
    }

    // Breaks when two databases of one cluster provisioning the same tenant at once fail on the role (roles are cluster-wide,
    // the advisory locks are per database): both must succeed and one role must exist, usable from both databases.
    [Fact]
    public async Task TwoDatabasesProvisioningTheSameTenantsAtOnceBothSucceedDespiteTheClusterWideRole()
    {
        await using var first = await TenantRig.StartAsync(postgres, maxPoolSize: 20, register: DocsAndAccess);
        await using var second = await TenantRig.StartAsync(postgres, maxPoolSize: 20, register: DocsAndAccess);
        var tenants = Enumerable.Range(0, 8).Select(_ => Guid.NewGuid()).ToList();
        using var start = new ManualResetEventSlim(false);

        Task<IReadOnlyList<TenantMigrationResult>> Run(TenantRig rig) => Task.Run(() =>
        {
            start.Wait(Token);
            return rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenants, 8, Token);
        }, Token);

        var one = Run(first);
        var two = Run(second);
        start.Set();
        var results = (await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromMinutes(3), Token)).SelectMany(r => r).ToList();

        Assert.Equal(16, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded, r.Error?.ToString()));
        foreach (var rig in new[] { first, second })
        {
            await using var scope = rig.ScopeFor(tenants[0]);
            Assert.Equal(rig.RoleOf(tenants[0]), await ScalarAsync(scope.ServiceProvider.GetRequiredService<AccessContext>(), UserSql));
        }

        var names = string.Join(", ", tenants.Select(t => $"'{first.RoleOf(t)}'"));
        Assert.Equal(8, await first.ScalarAsync<long>($"SELECT count(*) FROM pg_roles WHERE rolname IN ({names})"));
    }
}

// The rule that names a tenant's role. No server needed.
public class TenantRoleNameTests
{
    // Breaks when the name is not built from the database's oid and the id (a slug, a hash), changes its shape, or collides with
    // the schema name ("$user" in the default search_path would then reach the schema).
    [Fact]
    public void TheRoleNameIsMadeFromTheDatabaseOidAndTheTenantIdAndDiffersFromTheSchemaName()
    {
        var id = Guid.NewGuid();

        var role = TenantRoleName.For(16384, id);

        Assert.Equal($"t16384_{id:N}", role);
        Assert.NotEqual(TenantSchemaName.For(id), role);
        Assert.True(TenantRoleName.IsValid(role));
    }

    // Breaks when the same tenant gets the same role in two databases of a cluster (roles live in the whole cluster), or the
    // shared role is not the database's own.
    [Fact]
    public void TheSameTenantHasADifferentRoleInEachDatabaseAndEachDatabaseHasItsOwnSharedRole()
    {
        var id = Guid.NewGuid();

        Assert.NotEqual(TenantRoleName.For(16384, id), TenantRoleName.For(16385, id));
        Assert.Equal("t16384_shared", TenantRoleName.SharedFor(16384));
        Assert.NotEqual(TenantRoleName.SharedFor(16384), TenantRoleName.SharedFor(16385));
        Assert.True(TenantRoleName.IsValid(TenantRoleName.SharedFor(4294967295)));
    }

    // Breaks when a name that could end a quoted identifier, or another shape of name, is accepted for SQL.
    [Theory]
    [InlineData("t16384_0f8fad5bd9cb469fa16570867728950e\n")]
    [InlineData("t16384_0F8FAD5BD9CB469FA16570867728950E")]
    [InlineData("t16384_0f8fad5bd9cb469fa16570867728950e\"; DROP ROLE x; --")]
    [InlineData("t0_shared")]
    [InlineData("t016384_shared")]
    [InlineData("t16384_shared\n")]
    [InlineData("tenant_shared")]
    [InlineData("tenant_0f8fad5bd9cb469fa16570867728950e_role")]
    [InlineData("postgres")]
    [InlineData("")]
    public void ANameThatIsNotATenantRoleIsRefusedBeforeAnySql(string name)
    {
        Assert.False(TenantRoleName.IsValid(name));
        Assert.Throws<ArgumentException>(() => TenantRoleName.Validate(name));
    }

    // Breaks when the empty id or the database oid 0 gets a role.
    [Fact]
    public void TheEmptyIdAndTheZeroOidHaveNoRole()
    {
        Assert.Throws<ArgumentException>(() => TenantRoleName.For(16384, Guid.Empty));
        Assert.Throws<ArgumentException>(() => TenantRoleName.For(0, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => TenantRoleName.SharedFor(0));
    }
}
