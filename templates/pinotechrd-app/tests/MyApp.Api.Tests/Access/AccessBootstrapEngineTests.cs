using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web.Authorization;
using System.Diagnostics;
using MyApp.Modules.Access.Application.Security;
using Npgsql;

namespace MyApp.Api.Tests.Access;

// The administrator the migrator's bootstrap command creates can use every permission of the Access module through the
// security engine of the real API host, which runs as the application login, acting as the tenant role, on the tenant's own
// tables and the catalog the migrator seeded (plan, subscription and module rows linked by their ids). Real PostgreSQL.
[Collection(PostgresContainer.Name)]
public class AccessBootstrapEngineTests(PostgresFixture postgres)
{
    private const string Slug = "operator-ops";

    private const string Email = "root.admin@example.test";

    private const string Password = "correct-horse-battery-staple-1";

    private static string RouteOf(string permission) => "/bootstrap-fixtures/" + permission.ToLowerInvariant();

    // Breaks when the seeded plan, subscription (linked to the tenant by its id), module or permission rows, or the role's
    // grants, do not let the engine grant a permission of the module to the administrator, for any of the module's
    // permissions; the control (a user nobody made an administrator) proves the answers are not an open default.
    [Fact]
    public async Task TheBootstrapAdministratorCanAccessEveryAccessPermissionThroughTheSecurityEngineAsTheApplicationLogin()
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
            var migrated = await RunMigratorAsync(null, common);
            Assert.True(migrated.Exit == 0, migrated.Output);
            var bootstrapped = await RunMigratorAsync(Password, ["bootstrap", .. common, $"--Bootstrap:Slug={Slug}", "--Bootstrap:Name=Operator", $"--Bootstrap:AdminEmail={Email}"]);
            Assert.True(bootstrapped.Exit == 0, bootstrapped.Output);

            var (tenant, user) = await ReadAdministratorAsync(asMigrator);
            var factory = AccessHost.WithFixtures(new ApiHostFactory().WithDatabase(asApplication))
                .WithEndpoints(routes =>
                {
                    foreach (var permission in AccessPermissions.All)
                    {
                        routes.MapGet(RouteOf(permission), () => Results.Ok()).RequirePermission(permission);
                    }
                });
            using var _ = factory;
            using var client = factory.CreateClient();

            foreach (var permission in AccessPermissions.All)
            {
                using var granted = await client.GetWithBearerAsync("/api/v1" + RouteOf(permission), AccessJwt.TokenFor(user, tenant));
                Assert.True(granted.StatusCode == HttpStatusCode.OK, $"{permission}: {(int)granted.StatusCode}");
            }

            using var stranger = await client.GetWithBearerAsync("/api/v1" + RouteOf(AccessPermissions.UsersRead), AccessJwt.TokenFor(Guid.NewGuid(), tenant));
            Assert.NotEqual(HttpStatusCode.OK, stranger.StatusCode);
            Assert.DoesNotContain(Password, bootstrapped.Output, StringComparison.Ordinal);
        }
        finally
        {
            await AdminAsync($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
            await AdminAsync($"DROP OWNED BY {migrator}; DROP OWNED BY {application}");
            await AdminAsync($"DROP ROLE IF EXISTS {application}; DROP ROLE IF EXISTS {migrator}");
        }
    }

    // The built migrator, run as its own process (it is a second composition root and not a reference of the host's tests);
    // the password goes in its environment, as the command requires. Output is standard output and standard error together.
    internal static async Task<(int Exit, string Output)> RunMigratorAsync(string? password, string[] arguments)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
        var dll = Path.Combine(SolutionLocator.SolutionRoot(), "src", "MyApp.Migrator", "bin", configuration, "net10.0", "MyApp.Migrator.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll);
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment.Remove("Bootstrap__AdminPassword");
        if (password is not null)
        {
            start.Environment["Bootstrap__AdminPassword"] = password;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await output + await error);
    }

    private static async Task<(Guid Tenant, Guid User)> ReadAdministratorAsync(string migratorConnection)
    {
        await using var connection = new NpgsqlConnection(migratorConnection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var tenantCommand = new NpgsqlCommand("SELECT id FROM catalog.tenants WHERE identifier = $1", connection);
        tenantCommand.Parameters.AddWithValue(Slug);
        var tenant = (Guid)(await tenantCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        var schema = MyApp.BuildingBlocks.Infrastructure.Tenancy.TenantSchemaName.For(tenant);
#pragma warning disable CA2100 // The schema name is derived from a GUID.
        await using var userCommand = new NpgsqlCommand($"SELECT id FROM \"{schema}\".users WHERE email = $1", connection);
#pragma warning restore CA2100
        userCommand.Parameters.AddWithValue(Email);
        return (tenant, (Guid)(await userCommand.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    private async Task AdminAsync(string sql)
    {
        var admin = postgres.Connection("postgres");
        admin.Pooling = false;
        await using var connection = new NpgsqlConnection(admin.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
#pragma warning disable CA2100 // Generated names only.
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
