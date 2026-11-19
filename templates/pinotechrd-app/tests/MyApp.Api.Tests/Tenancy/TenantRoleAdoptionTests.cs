using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

#pragma warning disable EF1002, CA2100, CA1031, CA1849, CA2007, CA1307, CA1305, CA1310, MA0002, MA0004, MA0006, MA0011, MA0074, MA0076 // SQL is written by the tests, with names made from GUIDs.

namespace MyApp.Api.Tests.Tenancy;

// A tenant role the migrator did not make itself must never be adopted: someone else made it (or can still administer it, or
// is a member of it), so they could read the tenant's data through it. Real PostgreSQL (Docker); every login and database
// made here is dropped.
[Collection(PostgresContainer.Name)]
public class TenantRoleAdoptionTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static readonly string[] DeniedOrAbsent = ["42501", "3F000", "42P01"];

    private static string Q(string name) => "\"" + name + "\"";

    private static void MigratorModules(IServiceCollection services, string applicationRole)
    {
        services.AddTenantDbContext<DocsContext>("Docs");
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator(options => options.ApplicationRole = applicationRole);
    }

    private async Task<string> NewDatabaseAsync() => new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()).Database!;

    private async Task AdminAsync(string database, string sql)
    {
        var connection = postgres.Connection(database);
        connection.Pooling = false;
        await using var open = new NpgsqlConnection(connection.ConnectionString);
        await open.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, open);
        await command.ExecuteNonQueryAsync(Token);
    }

    private async Task<string> ScalarAsync(string database, string sql)
    {
        var connection = postgres.Connection(database);
        connection.Pooling = false;
        await using var open = new NpgsqlConnection(connection.ConnectionString);
        await open.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, open);
        return Convert.ToString(await command.ExecuteScalarAsync(Token)) ?? "null";
    }

    private async Task<NpgsqlConnection> OpenAsAsync(string database, string user, string password)
    {
        var connection = postgres.Connection(database);
        connection.Username = user;
        connection.Password = password;
        connection.Pooling = false;
        var open = new NpgsqlConnection(connection.ConnectionString);
        await open.OpenAsync(Token);
        return open;
    }

    private async Task<string> ReadAsAsync(string database, string user, string password, string sql)
    {
        await using var open = await OpenAsAsync(database, user, password);
        await using var command = new NpgsqlCommand(sql, open);
        return Convert.ToString(await command.ExecuteScalarAsync(Token)) ?? "null";
    }

    private string RoleOf(string database, Guid tenant) => TenantRoles.For(postgres.Connection(database).ConnectionString, tenant);

    private TenantRig SuperuserRig(string database, string applicationRole)
    {
        var connection = postgres.Connection(database);
        connection.MaxPoolSize = 8;
        return TenantRig.StartOn(
            connection.ConnectionString, new NpgsqlDataSourceBuilder(connection.ConnectionString).Build(), ownsDataSource: true, s => MigratorModules(s, applicationRole));
    }

    // A database owned by a migrator login that can create roles and is not a superuser.
    private async Task<(string Database, DedicatedLogin Migrator, DedicatedLogin Application)> DeployTwoLoginsAsync()
    {
        var migrator = await DedicatedLogin.CreateAsync(postgres);
        var application = await DedicatedLogin.CreateAsync(postgres);
        var database = await NewDatabaseAsync();
        migrator.Drops(database);
        application.Drops(database);
        await AdminAsync(database, $"ALTER ROLE {migrator.Name} CREATEROLE; ALTER DATABASE \"{database}\" OWNER TO {migrator.Name}");
        return (database, migrator, application);
    }

    private async Task DropRolesAsync(params string[] roles)
    {
        foreach (var role in roles)
        {
            try
            {
                await AdminAsync("postgres", "DROP ROLE IF EXISTS " + Q(role));
            }
            catch (PostgresException)
            {
                // Best effort: a leftover role does not affect another test (names carry the database's oid).
            }
        }
    }

    private static string FirstLine(TenantMigrationResult result) => result.Error is null ? "ok" : result.Error.Message.Split('\n')[0];

    // Breaks when the migrator adopts a tenant role another login created WITH LOGIN in a transaction that commits after the
    // migrator already looked (and found no role) and is waiting on the name: the role keeps its login and password, and
    // the migrator must refuse it, not grant the application login a membership or give it schema rights.
    [Fact]
    public async Task ALoginRoleCommittedByAnotherLoginWhileTheMigratorWaitsOnItIsRefusedAndTheTenantSchemaStaysUnusableThroughIt()
    {
        var tenant = Guid.NewGuid();
        var attacker = await DedicatedLogin.CreateAsync(postgres);
        var application = await DedicatedLogin.CreateAsync(postgres);
        var database = await NewDatabaseAsync();
        application.Drops(database);
        await AdminAsync(database, $"ALTER ROLE {attacker.Name} CREATEROLE");
        var role = RoleOf(database, tenant);
        var shared = TenantRoles.SharedOf(postgres.Connection(database).ConnectionString);
        try
        {
            await using var creator = await OpenAsAsync(database, attacker.Name, attacker.Password);
            await using (var begin = new NpgsqlCommand("BEGIN", creator))
            {
                await begin.ExecuteNonQueryAsync(Token);
            }

            await using (var create = new NpgsqlCommand($"CREATE ROLE {Q(role)} LOGIN PASSWORD 'pw-race'", creator))
            {
                await create.ExecuteNonQueryAsync(Token);
            }

            await using var migrator = SuperuserRig(database, application.Name);
            var run = migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

            // Wait until the migrator is really blocked on the uncommitted role, not for a fixed time.
            var blocked = false;
            for (var i = 0; i < 120 && !blocked && !run.IsCompleted; i++)
            {
                blocked = await ScalarAsync(database, $"SELECT count(*) FROM pg_stat_activity WHERE datname = '{database}' AND wait_event_type = 'Lock'") != "0";
                if (!blocked)
                {
                    await Task.Delay(250, Token);
                }
            }

            Assert.True(blocked, "The migrator never waited on the uncommitted role: the race was not set up.");
            Assert.False(run.IsCompleted);

            await using (var commit = new NpgsqlCommand("COMMIT", creator))
            {
                await commit.ExecuteNonQueryAsync(Token);
            }

            var result = await run;

            Assert.False(result.Succeeded, "The migrator adopted a role another login made WITH LOGIN.");
            Assert.Equal("0", await ScalarAsync(
                database,
                "SELECT count(*) FROM pg_auth_members m JOIN pg_roles r ON r.oid = m.roleid JOIN pg_roles u ON u.oid = m.member "
                + $"WHERE r.rolname = '{role}' AND u.rolname = '{application.Name}'"));
            await Assert.ThrowsAnyAsync<PostgresException>(() =>
                ReadAsAsync(database, role, "pw-race", $"SELECT count(*) FROM {Q(TenantSchemaName.For(tenant))}.docs_items"));
        }
        finally
        {
            await application.DisposeAsync();
            await DropRolesAsync(role, shared);
            await attacker.DisposeAsync();
        }
    }

    // Breaks when a clean NOLOGIN tenant role made by another CREATEROLE login is adopted: that login keeps ADMIN on it and
    // could grant itself the role, so the migration must fail naming the role, and nothing is granted on the schema.
    [Fact]
    public async Task ACleanNologinTenantRoleMadeByAnotherCreateRoleLoginFailsTheMigrationNamingTheRoleAndItsCreatorStillCannotReadTenantData()
    {
        var (database, migratorLogin, application) = await DeployTwoLoginsAsync();
        var creator = await DedicatedLogin.CreateAsync(postgres);
        var tenant = Guid.NewGuid();
        await AdminAsync(database, $"ALTER ROLE {creator.Name} CREATEROLE");
        var role = RoleOf(database, tenant);
        var shared = TenantRoles.SharedOf(postgres.Connection(database).ConnectionString);
        try
        {
            await using (var open = await OpenAsAsync(database, creator.Name, creator.Password))
            await using (var create = new NpgsqlCommand($"CREATE ROLE {Q(role)} NOLOGIN", open))
            {
                await create.ExecuteNonQueryAsync(Token);
            }

            await using (var migrator = migratorLogin.RigOn(database, s => MigratorModules(s, application.Name), maxPoolSize: 6))
            {
                var result = await migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

                Assert.False(result.Succeeded, "The migrator adopted a role another login made.");
                Assert.Contains(role, result.Error!.Message, StringComparison.Ordinal);
            }

            // The creator may even grant itself the role (it administers it); that must still give it nothing.
            var read = await Assert.ThrowsAnyAsync<PostgresException>(async () =>
            {
                await using var open = await OpenAsAsync(database, creator.Name, creator.Password);
                await using (var grant = new NpgsqlCommand($"GRANT {Q(role)} TO {creator.Name} WITH INHERIT TRUE, SET TRUE", open))
                {
                    try
                    {
                        await grant.ExecuteNonQueryAsync(Token);
                    }
                    catch (PostgresException)
                    {
                        // Refused is as good as granted for this test: either way the read below must fail.
                    }
                }

                await using var select = new NpgsqlCommand($"SELECT count(*) FROM {Q(TenantSchemaName.For(tenant))}.docs_items", open);
                await select.ExecuteScalarAsync(Token);
            });
            Assert.Contains(read.SqlState, DeniedOrAbsent);
        }
        finally
        {
            await migratorLogin.DisposeAsync();
            await application.DisposeAsync();
            await DropRolesAsync(role, shared);
            await creator.DisposeAsync();
        }
    }

    // Breaks when a pre-existing NOLOGIN tenant role that already has a member (GRANT role TO someone) is adopted: that
    // member reads the tenant's data through the role, whoever made it.
    [Fact]
    public async Task APreexistingNologinTenantRoleThatHasAnExtraMemberFailsTheMigration()
    {
        var tenant = Guid.NewGuid();
        var application = await DedicatedLogin.CreateAsync(postgres);
        var member = await DedicatedLogin.CreateAsync(postgres);
        var database = await NewDatabaseAsync();
        application.Drops(database);
        var role = RoleOf(database, tenant);
        var shared = TenantRoles.SharedOf(postgres.Connection(database).ConnectionString);
        try
        {
            await AdminAsync(database, $"CREATE ROLE {Q(role)} NOLOGIN; GRANT {Q(role)} TO {member.Name}");
            await using var migrator = SuperuserRig(database, application.Name);

            var result = await migrator.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

            Assert.False(result.Succeeded, "The migrator adopted a role that already has a member.");
            Assert.Contains(role, FirstLine(result) + result.Error!.Message, StringComparison.Ordinal);
        }
        finally
        {
            await application.DisposeAsync();
            await DropRolesAsync(role, shared);
            await member.DisposeAsync();
        }
    }

    // Breaks when a second migrate run refuses roles the migrator made itself (its own memberships and its own admin
    // option on them must be accepted), with a superuser migrator.
    [Fact]
    public async Task ASecondMigrateRunOverRolesTheSuperuserMigratorMadeItselfSucceeds()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var application = await DedicatedLogin.CreateAsync(postgres);
        var database = await NewDatabaseAsync();
        application.Drops(database);
        var roles = new[] { RoleOf(database, a), RoleOf(database, b), TenantRoles.SharedOf(postgres.Connection(database).ConnectionString) };
        try
        {
            await using var migrator = SuperuserRig(database, application.Name);
            var service = migrator.Services.GetRequiredService<ITenantMigrator>();
            Assert.All(await service.MigrateAsync([a, b], 2, Token), r => Assert.True(r.Succeeded, r.Error?.ToString()));

            var second = await service.MigrateAsync([a, b], 2, Token);

            Assert.All(second, r => Assert.True(r.Succeeded, r.Error?.ToString()));
        }
        finally
        {
            await application.DisposeAsync();
            await DropRolesAsync(roles);
        }
    }

    // Same control with the two-login deployment: a migrator that is not a superuser, and the application login member of
    // each role. After the second run the application login still works as the tenant.
    [Fact]
    public async Task ASecondMigrateRunOverRolesTheNonSuperuserMigratorMadeItselfSucceedsAndTheApplicationLoginStillWorks()
    {
        var (database, migratorLogin, application) = await DeployTwoLoginsAsync();
        var tenant = Guid.NewGuid();
        var role = RoleOf(database, tenant);
        var shared = TenantRoles.SharedOf(postgres.Connection(database).ConnectionString);
        try
        {
            await using (var migrator = migratorLogin.RigOn(database, s => MigratorModules(s, application.Name), maxPoolSize: 6))
            {
                var service = migrator.Services.GetRequiredService<ITenantMigrator>();
                var first = await service.MigrateAsync(tenant, Token);
                Assert.True(first.Succeeded, first.Error?.ToString());

                var second = await service.MigrateAsync(tenant, Token);

                Assert.True(second.Succeeded, second.Error?.ToString());
            }

            await using var app = application.RigOn(database, s =>
            {
                s.AddTenantDbContext<DocsContext>("Docs");
                s.AddTenantDbContext<AccessContext>("Access");
            }, maxPoolSize: 2);
            await using var scope = app.ScopeFor(tenant);
            var docs = scope.ServiceProvider.GetRequiredService<DocsContext>();
            docs.Docs.Add(new DocItem { Title = "after second run" });
            Assert.Equal(1, await docs.SaveChangesAsync(Token));
        }
        finally
        {
            await migratorLogin.DisposeAsync();
            await application.DisposeAsync();
            await DropRolesAsync(role, shared);
        }
    }
}
