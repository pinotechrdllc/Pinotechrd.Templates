using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;
using Npgsql;
using Pinotechrd.Security.Enums;

namespace MyApp.Api.Tests.Access;

// The catalog's tenant directory as the API host has it, on a database the real migrator made and bootstrapped, with a
// migrator login (not a superuser) and an application login of its own: the host runs as the application login, which
// reads only the catalog columns the migrator granted it. Real PostgreSQL; the logins and the database are dropped.
[Collection(PostgresContainer.Name)]
public class CatalogTenantDirectoryTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when the API host has no directory or another one, when the in-service list keeps a Provisioning or Cancelled
    // tenant or the empty id, drops an Active or Suspended one, is not ordered by id, or when reading it as the application
    // login (column grants on catalog.tenants only, no superuser, not the owner) fails with a permission error because the
    // query touches a column the login may not read.
    [Fact]
    public async Task FromTheApiHostAsTheApplicationLoginTheInServiceListIsTheActiveAndSuspendedTenantsOrderedById()
    {
        var suffix = Guid.NewGuid().ToString("N")[..16];
        var (migrator, application, secret) = ("m_" + suffix, "a_" + suffix, "Pw-" + Guid.NewGuid().ToString("N"));
        var superuser = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync());
        var database = superuser.Database!;
        await AdminAsync($"CREATE ROLE {migrator} LOGIN CREATEROLE PASSWORD '{secret}'; CREATE ROLE {application} LOGIN PASSWORD '{secret}'");
        try
        {
            await AdminAsync($"ALTER DATABASE \"{database}\" OWNER TO {migrator}");
            var asMigrator = new NpgsqlConnectionStringBuilder(superuser.ConnectionString) { Username = migrator, Password = secret, Pooling = false }.ConnectionString;
            var asApplication = new NpgsqlConnectionStringBuilder(superuser.ConnectionString) { Username = application, Password = secret }.ConnectionString;
            string[] common = [$"--ConnectionStrings:Database={asMigrator}", $"--Migrator:ApplicationRole={application}"];
            var migrated = await AccessBootstrapEngineTests.RunMigratorAsync(null, common);
            Assert.True(migrated.Exit == 0, migrated.Output);
            var bootstrapped = await AccessBootstrapEngineTests.RunMigratorAsync(
                "correct-horse-battery-staple-1",
                ["bootstrap", .. common, "--Bootstrap:Slug=directory-ops", "--Bootstrap:Name=Operator", "--Bootstrap:AdminEmail=root.admin@example.test"]);
            Assert.True(bootstrapped.Exit == 0, bootstrapped.Output);

            // The bootstrapped tenant is Active; the other rows copy it with another id, slug, schema and status, as the
            // migrator (the owner of the catalog) would write them. They need no schema: the directory reads the catalog only.
            await using var owner = new NpgsqlConnection(asMigrator);
            await owner.OpenAsync(Token);
            var bootstrappedTenant = (Guid)(await ScalarAsync(owner, "SELECT id FROM catalog.tenants WHERE identifier = 'directory-ops'"))!;
            Assert.Equal((int)TenantStatus.Active, Convert.ToInt32(await ScalarAsync(owner, $"SELECT status FROM catalog.tenants WHERE id = '{bootstrappedTenant}'"), CultureInfo.InvariantCulture));
            var rows = new List<(Guid Id, TenantStatus Status)>
            {
                (Guid.NewGuid(), TenantStatus.Active),
                (Guid.NewGuid(), TenantStatus.Suspended),
                (Guid.NewGuid(), TenantStatus.Active),
                (Guid.NewGuid(), TenantStatus.Suspended),
                (Guid.NewGuid(), TenantStatus.Provisioning),
                (Guid.NewGuid(), TenantStatus.Cancelled),
                (Guid.Empty, TenantStatus.Active),
            };
            foreach (var (id, status) in rows)
            {
                var slug = "t-" + id.ToString("N");
                await ScalarAsync(owner, $"""
                    CREATE TEMP TABLE copy AS SELECT * FROM catalog.tenants WHERE id = '{bootstrappedTenant}';
                    UPDATE copy SET id = '{id}', identifier = '{slug}', name = '{slug}', db_schema = 'tenant_{id:N}', status = {(int)status};
                    INSERT INTO catalog.tenants SELECT * FROM copy;
                    DROP TABLE copy;
                    """);
            }

            var expected = rows.Where(r => r.Id != Guid.Empty && r.Status is TenantStatus.Active or TenantStatus.Suspended)
                .Select(r => r.Id)
                .Append(bootstrappedTenant)
                .OrderBy(id => id.ToString("D"), StringComparer.Ordinal)
                .ToList();

            using var factory = AccessHost.WithFixtures(new ApiHostFactory().WithDatabase(asApplication));
            await using var scope = factory.Services.CreateAsyncScope();
            var directory = scope.ServiceProvider.GetRequiredService<ITenantDirectory>();
            Assert.IsType<CatalogTenantDirectory>(directory);

            var listed = await directory.ListInServiceTenantIdsAsync(Token);

            Assert.Equal(expected, listed);

            // The host really is the application login, which is neither a superuser nor the catalog's owner, and which may
            // not read the catalog columns it was not granted: the list above was read under the narrow grants.
            var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
            await using var connection = await dataSource.OpenConnectionAsync(Token);
            Assert.Equal(application, await ScalarAsync(connection, "SELECT current_user"));
            Assert.Equal(false, await ScalarAsync(connection, "SELECT rolsuper FROM pg_roles WHERE rolname = current_user"));
            Assert.Equal(migrator, await ScalarAsync(connection, "SELECT tableowner FROM pg_tables WHERE schemaname = 'catalog' AND tablename = 'tenants'"));
            var refused = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(connection, "SELECT name FROM catalog.tenants LIMIT 1"));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
        }
        finally
        {
            await AdminAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await AdminAsync($"DROP OWNED BY {migrator}; DROP OWNED BY {application}");
            await AdminAsync($"DROP ROLE IF EXISTS {application}; DROP ROLE IF EXISTS {migrator}");
        }
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // Built from GUIDs and fixed names only.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return await command.ExecuteScalarAsync(Token);
    }

    private async Task AdminAsync(string sql)
    {
        var admin = postgres.Connection("postgres");
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(Token);
#pragma warning disable CA2100 // Generated names only.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(Token);
    }
}
