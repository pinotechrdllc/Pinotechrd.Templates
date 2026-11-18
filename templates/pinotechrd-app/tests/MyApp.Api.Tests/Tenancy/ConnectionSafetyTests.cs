using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// Connection settings that break the tenant's session state: refused when the host starts and by any data source a tenant
// context or the migrator uses. None of the refusals needs Docker: they happen before any connection.
public class ConnectionSafetyRefusalTests
{
    private const string Password = "Pw-Secret-8e12";
    private const string User = "svc_user_cs";
    private const string Host = "db.safety.example";

    private static string ConnectionString(string setting) =>
        $"Host={Host};Database=app;Username={User};Password={Password};{setting}";

    public static TheoryData<string, string> Unsafe() =>
        new()
        {
            { "Multiplexing=true", "Multiplexing" },
            { "No Reset On Close=true", "No Reset On Close" },
            { "Search Path=other_schema", "Search Path" },
            { "Options=-c search_path=other_schema", "search_path" },
            { "Options=-csearch_path=other_schema", "search_path" },
            { "Options=--search_path=other_schema", "search_path" },
            { "Options=-c search-path=other_schema", "search_path" },
            { "Options=-c SEARCH_PATH=other_schema", "search_path" },
            { "Options=-c statement_timeout=5 -c search_path=other_schema", "search_path" },
            { "Options=-c search\\_path=other_schema", "search_path" },
            { "Options=-c sea\\rch_path=other_schema", "search_path" },
            { "Options=--search-path=other_schema", "search_path" },
            { "Options=-c Search_Path=other_schema", "search_path" },
            { "Options=\"-c search_path=other_schema\"", "search_path" },
            { "Options='-c search_path=other_schema'", "search_path" },
            { "Options=-c work_mem=1MB -c search_path=other_schema", "search_path" },
            { "Options=-c application_name=app --search-path=other_schema", "search_path" },
        };

    // Breaks when a connection string with an unsafe setting starts the host, when the error does not name the setting, or
    // when any text of the failure quotes the password, the user, the host or the other schema.
    [Theory]
    [MemberData(nameof(Unsafe))]
    public void AnUnsafeConnectionStringStopsTheHostAndTheErrorNamesTheSettingOnly(string setting, string token)
    {
        using var factory = new ApiHostFactory().WithDatabase(ConnectionString(setting));

        var message = StartupFailure.AssertOptionsInvalidWithout(factory, token, Password, User, Host, "other_schema");

        Assert.Contains("ConnectionStrings:Database", message, StringComparison.Ordinal);
    }

    private static NpgsqlDataSource HandMade(string setting) => new NpgsqlDataSourceBuilder(ConnectionString(setting)).Build();

    // Breaks when a data source made by hand with an unsafe setting is accepted by a tenant context. The failure comes before
    // any connection (nothing listens on the host, so a connection attempt would give another exception), names the
    // setting, and quotes no secret.
    [Theory]
    [MemberData(nameof(Unsafe))]
    public void ATenantContextRefusesAHandMadeDataSourceWithAnUnsafeSettingBeforeAnyCommand(string setting, string token)
    {
        using var dataSource = HandMade(setting);
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddTenantDbContext<AccessContext>("Access");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AccessContext>());

        Assert.Contains(token, error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoSecret(error.ToString());
    }

    // Breaks when the migrator accepts the same data sources.
    [Theory]
    [MemberData(nameof(Unsafe))]
    public void TheMigratorRefusesAHandMadeDataSourceWithAnUnsafeSettingBeforeAnyCommand(string setting, string token)
    {
        using var dataSource = HandMade(setting);
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ITenantMigrator>());

        Assert.Contains(token, error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoSecret(error.ToString());
    }

    // Breaks when a context configured outside dependency injection (UseTenantPersistence on a builder) skips the check.
    [Theory]
    [MemberData(nameof(Unsafe))]
    public void UseTenantPersistenceRefusesAnUnsafeDataSourceToo(string setting, string token)
    {
        using var dataSource = HandMade(setting);
        var tenant = new ClaimedTenant(new SchemaHolder { Schema = "tenant_x" });

        var error = Assert.Throws<InvalidOperationException>(
            () => new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AccessContext>().UseTenantPersistence(dataSource, tenant, "Access"));

        Assert.Contains(token, error.Message, StringComparison.OrdinalIgnoreCase);
        AssertNoSecret(error.ToString());
    }

    // Breaks when a safe setting is refused: Pooling=false, a statement timeout in Options and a prepared-statement setting
    // are legal.
    [Theory]
    [InlineData("Pooling=false")]
    [InlineData("Options=-c statement_timeout=5000")]
    [InlineData("Options=-c application_name=search_path_app")]
    [InlineData("Max Auto Prepare=10")]
    [InlineData("Multiplexing=false")]
    public void SafeSettingsAreAccepted(string setting)
    {
        using var dataSource = HandMade(setting);
        var services = new ServiceCollection();
        services.AddSingleton(dataSource);
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AccessContext>());
        Assert.NotNull(provider.GetRequiredService<ITenantMigrator>());
    }

    private static void AssertNoSecret(string text)
    {
        Assert.DoesNotContain(Password, text, StringComparison.Ordinal);
        Assert.DoesNotContain(User, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Host, text, StringComparison.Ordinal);
        Assert.DoesNotContain("other_schema", text, StringComparison.Ordinal);
    }
}

// The settings against a real server. Needs Docker.
[Collection(PostgresContainer.Name)]
public class ConnectionSafetyServerTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when isolation depends on pooling: with Pooling=false every open is a new physical connection and every close
    // ends it, so alternating tenants stay in their own schemas, and a raw connection has the default search_path.
    [Fact]
    public async Task WithPoolingOffTenantsAreStillIsolatedAndARawConnectionIsDefault()
    {
        await using var rig = await TenantRig.StartAsync(postgres, tune: c => c.Pooling = false);
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        await rig.ProvisionAsync(a);
        await rig.ProvisionAsync(b);

        for (var i = 0; i < 8; i++)
        {
            var tenant = i % 2 == 0 ? a : b;
            await using var scope = rig.ScopeFor(tenant);
            var context = scope.ServiceProvider.GetRequiredService<AccessContext>();
            var tag = tenant.ToString("N")[..8];
            context.Items.Add(new FixtureItem { Name = $"{tag}-{i}" });
            await context.SaveChangesAsync(Token);
            Assert.Equal(TenantSchemaName.For(tenant), await context.Database.SqlQueryRaw<string>("SELECT current_schema() AS \"Value\"").SingleAsync(Token));
            Assert.All(await context.Items.Select(x => x.Name).ToListAsync(Token), n => Assert.StartsWith(tag, n, StringComparison.Ordinal));
        }

        await using var raw = await rig.DataSource.OpenConnectionAsync(Token);
        await using var command = new NpgsqlCommand("SELECT current_schema()", raw);
        Assert.Equal("public", await command.ExecuteScalarAsync(Token));
    }

    // The hole the multiplexing refusal closes, shown on a data source built directly (the refusal is bypassed on purpose):
    // with Multiplexing the session setting made by one command is not there for the next, because commands run on whichever
    // physical connection is free. Breaks only if Npgsql changed multiplexing, in which case the refusal needs a new look.
    [Fact]
    public async Task WithMultiplexingASessionSettingDoesNotFollowTheNextCommands()
    {
        var connection = new NpgsqlConnectionStringBuilder(await postgres.CreateDatabaseAsync()) { Multiplexing = true, MaxPoolSize = 4 };
        await using var dataSource = new NpgsqlDataSourceBuilder(connection.ConnectionString).Build();
        await using (var set = dataSource.CreateCommand("SELECT set_config('search_path', 'multiplexing_probe', false)"))
        {
            await set.ExecuteScalarAsync(Token);
        }

        var seen = new ConcurrentBag<string>();
        await Parallel.ForEachAsync(
            Enumerable.Range(0, 300),
            new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = Token },
            async (_, ct) =>
            {
                await using var read = dataSource.CreateCommand("SELECT current_setting('search_path') FROM pg_sleep(0.02)");
                seen.Add((string)(await read.ExecuteScalarAsync(ct))!);
            });

        Assert.Contains("multiplexing_probe", seen);
        Assert.Contains(seen, s => !s.Contains("multiplexing_probe", StringComparison.Ordinal));
    }
}
