using Microsoft.EntityFrameworkCore;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.Migrator.Tests.Bootstrap;
using MyApp.Migrator.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;
using Npgsql;

namespace MyApp.Migrator.Tests.Tenancy;

// The two-login deployment: the migrator (not a superuser) migrates and bootstraps, and the API runs as a separate plain login.
// The request pipeline reads the tenant catalog as that login (a slug before login, the status of a token's tenant), so what
// the migrator grants must let it do exactly those reads: no 42501 (insufficient privilege), and no right to change a tenant.
[Collection(PostgresContainer.Name)]
public class TenantCatalogAsApplicationLoginTests(PostgresFixture postgres)
{
    private const int ActiveStatus = 1;

    private const int SuspendedStatus = 2;

    private static async Task<Guid> BootstrapAsync(SeparateLoginsDatabase logins)
    {
        await BootstrapSupport.MigrateAsync(logins.MigratorConnection, logins.RoleArgument);
        var run = await BootstrapRun.ExecuteAsync(logins.MigratorConnection, BootstrapRun.GoodPassword, logins.RoleArgument);
        Assert.True(run.ExitCode == 0, run.Report);
        return await BootstrapSupport.TenantAsync(logins.MigratorConnection);
    }

    private static async Task<NpgsqlConnection> OpenAsApplicationAsync(SeparateLoginsDatabase logins)
    {
        var connection = new NpgsqlConnection(logins.ApplicationConnection);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    private static async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql, string name, object value)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue(name, value);
        try
        {
            return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }
        catch (PostgresException error)
        {
            throw new Xunit.Sdk.XunitException($"The application login was refused ({error.SqlState}): {error.MessageText}");
        }
    }

    // Breaks when the application login lacks the right to read the tenants of the catalog after the migrator ran (the lookup
    // by slug of an anonymous request fails with 42501), or when the lookup does not find the active tenant.
    [Fact]
    public async Task TheApplicationLoginFindsTheActiveTenantBySlugAfterTheMigratorRan()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        var tenant = await BootstrapAsync(logins);
        await using var connection = await OpenAsApplicationAsync(logins);

        var found = await ScalarAsync(
            connection, $"SELECT id FROM catalog.tenants WHERE identifier = @slug AND status = {ActiveStatus}", "slug", BootstrapRun.DefaultSlug);
        var unknown = await ScalarAsync(
            connection, $"SELECT id FROM catalog.tenants WHERE identifier = @slug AND status = {ActiveStatus}", "slug", "nobody");

        Assert.Equal(tenant, found);
        Assert.Null(unknown);
    }

    // Breaks when the application login cannot read the status of a tenant by id (the check of every authenticated request).
    [Fact]
    public async Task TheApplicationLoginReadsTheStatusOfATenantByIdAfterTheMigratorRan()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        var tenant = await BootstrapAsync(logins);
        await using var connection = await OpenAsApplicationAsync(logins);

        var active = await ScalarAsync(
            connection, $"SELECT count(*) FROM catalog.tenants WHERE id = @id AND status = {ActiveStatus}", "id", tenant);
        var unknown = await ScalarAsync(
            connection, $"SELECT count(*) FROM catalog.tenants WHERE id = @id AND status = {ActiveStatus}", "id", Guid.NewGuid());

        Assert.Equal(1L, active);
        Assert.Equal(0L, unknown);
    }

    // Breaks when the grant is wide: the application login only reads the catalog's tenants, it never changes a status.
    [Fact]
    public async Task TheApplicationLoginCannotChangeTheStatusOfATenantInTheCatalog()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        var tenant = await BootstrapAsync(logins);
        await using var connection = await OpenAsApplicationAsync(logins);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE catalog.tenants SET status = {SuspendedStatus} WHERE id = @id";
        command.Parameters.AddWithValue("id", tenant);

        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));

        Assert.Equal("42501", error.SqlState);
    }

    private static async Task<string> RefusalAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // The SQL is written by this test; the names in it are constants.
        command.CommandText = sql;
#pragma warning restore CA2100
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        return error.SqlState;
    }

    // The provider and the naming of the run time (the design-time options carry them, without a connection), on the login's connection.
    private static CatalogDbContext CatalogContextAs(string connectionString) =>
        new(new DbContextOptionsBuilder<CatalogDbContext>(SharedPersistenceRegistration.DesignTimeOptions<CatalogDbContext>(CatalogDbContext.Schema))
            .UseNpgsql(connectionString)
            .Options);

    public static TheoryData<string> StatementsTheApplicationLoginMustBeRefused() =>
    [
        "SELECT name FROM catalog.tenants",
        "SELECT * FROM catalog.tenants",
        "SELECT id FROM catalog.tenants WHERE name = 'x'",
        "SELECT id FROM catalog.tenants ORDER BY name",
        "SELECT id, identifier, status, name FROM catalog.tenants",
        "SELECT id FROM catalog.subscription_plans",
        "SELECT id FROM catalog.system_subscriptions",
        "SELECT id FROM catalog.subscription_plan_modules",
        "SELECT id FROM catalog.subscription_plan_features",
        "SELECT * FROM catalog.__ef_migrations_catalog",
        "INSERT INTO catalog.tenants (id) VALUES ('00000000-0000-0000-0000-000000000001')",
        "UPDATE catalog.tenants SET identifier = 'x'",
        "UPDATE catalog.tenants SET status = 2",
        "DELETE FROM catalog.tenants",
        "TRUNCATE catalog.tenants",
        "INSERT INTO catalog.subscription_plans (id) VALUES ('00000000-0000-0000-0000-000000000001')",
        "DELETE FROM catalog.__ef_migrations_catalog",
    ];

    // Breaks when the grant is wider than the three columns the lookups use (name, the other catalog tables and the EF history
    // table stay unreadable) or when the application login may write: each statement must be refused with 42501.
    [Theory]
    [MemberData(nameof(StatementsTheApplicationLoginMustBeRefused))]
    public async Task TheApplicationLoginIsRefusedAnythingBeyondReadingIdIdentifierAndStatusOfTheTenants(string sql)
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        await BootstrapAsync(logins);
        await using var connection = await OpenAsApplicationAsync(logins);

        Assert.Equal("42501", await RefusalAsync(connection, sql));
    }

    // Breaks when the grant misses one of the three columns the lookups use, or the schema usage.
    [Fact]
    public async Task TheApplicationLoginReadsIdIdentifierAndStatusOfTheTenants()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        var tenant = await BootstrapAsync(logins);
        await using var connection = await OpenAsApplicationAsync(logins);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, identifier, status FROM catalog.tenants";
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);

        Assert.True(await reader.ReadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(tenant, reader.GetGuid(0));
        Assert.Equal(BootstrapRun.DefaultSlug, reader.GetString(1));
        Assert.Equal(ActiveStatus, reader.GetInt32(2));
    }

    // Breaks when the real query of CatalogTenantCatalog reads a column the application login may not read (a mapped extra
    // column, a join, a changed projection): EF would fail with 42501 here, as it would in production.
    [Fact]
    public async Task TheRealCatalogQueriesRunAsTheApplicationLoginUnderTheGrantsOfTheMigrator()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        var tenant = await BootstrapAsync(logins);
        var suspended = Guid.NewGuid();
        await CatalogSeed.AddTenantRowAsync(
            logins.MigratorConnection, suspended, await CatalogSeed.AddSubscriptionAsync(logins.MigratorConnection), "other-one", "Other", SuspendedStatus);
        await using var context = CatalogContextAs(logins.ApplicationConnection);
        var catalog = new CatalogTenantCatalog(context);

        var bySlug = await catalog.FindActiveTenantIdAsync(BootstrapRun.DefaultSlug, TestContext.Current.CancellationToken);
        var unknown = await catalog.FindActiveTenantIdAsync("nobody", TestContext.Current.CancellationToken);
        var suspendedBySlug = await catalog.FindActiveTenantIdAsync("other-one", TestContext.Current.CancellationToken);
        var active = await catalog.IsActiveAsync(tenant, TestContext.Current.CancellationToken);
        var notActive = await catalog.IsActiveAsync(suspended, TestContext.Current.CancellationToken);
        var absent = await catalog.IsActiveAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        Assert.Equal(tenant, bySlug);
        Assert.Null(unknown);
        Assert.Null(suspendedBySlug);
        Assert.True(active);
        Assert.False(notActive);
        Assert.False(absent);
    }

    // Breaks when naming the migrator's own login as the application role grants (or fails): the owner needs nothing, so the
    // run succeeds and no column of the tenants carries a grant, nor does another login get the schema.
    [Fact]
    public async Task AnApplicationRoleEqualToTheMigratorLoginMigratesAndGrantsNothingExtra()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);

        await BootstrapSupport.MigrateAsync(logins.MigratorConnection, $"--{SeparateLoginsDatabase.ApplicationRoleKey}={logins.Migrator}");

        Assert.Equal(
            0L,
            await Db.CountAsync(
                logins.MigratorConnection,
                "SELECT count(*) FROM pg_attribute WHERE attrelid = 'catalog.tenants'::regclass AND attacl IS NOT NULL"));
        Assert.Equal(
            0L,
            await Db.CountAsync(
                logins.MigratorConnection,
                $"SELECT count(*) FROM pg_namespace WHERE nspname = 'catalog' AND coalesce(nspacl::text, '') LIKE '%{logins.Application}%'"));
        await using var other = await OpenAsApplicationAsync(logins);
        Assert.Equal("42501", await RefusalAsync(other, "SELECT id FROM catalog.tenants"));
    }

    // Breaks when a missing application login is ignored by the grant step of the shared schemas (no tenant exists yet, so the
    // tenant roles cannot be what fails): the run exits 1 and the report names Migrator:ApplicationRole.
    [Fact]
    public async Task AnApplicationRoleThatDoesNotExistFailsTheRunBeforeAnyTenantExists()
    {
        await using var logins = await SeparateLoginsDatabase.CreateAsync(postgres);
        var missing = "no_such_" + Guid.NewGuid().ToString("N")[..12];

        var run = await MigratorRun.ExecuteAsync(logins.MigratorConnection, $"--{SeparateLoginsDatabase.ApplicationRoleKey}={missing}");

        Assert.Equal(1, run.ExitCode);
        Assert.Contains(SeparateLoginsDatabase.ApplicationRoleKey, run.Report, StringComparison.Ordinal);
    }
}
