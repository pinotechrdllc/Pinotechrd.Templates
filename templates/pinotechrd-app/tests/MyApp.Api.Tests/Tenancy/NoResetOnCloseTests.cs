using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// A pooled connection keeps the tenant's search_path until Npgsql resets it, so a data source that does not reset
// connections must be refused. No Docker: the refusals happen before any connection.
public class NoResetOnCloseTests
{
    private const string Password = "Pw-Secret-4d71";
    private const string User = "svc_user_nr";
    private const string Host = "db.noreset.example";

    private static string ConnectionString(string setting) =>
        $"Host={Host};Database=app;Username={User};Password={Password};{setting}";

    // Breaks when a connection string with No Reset On Close (any spelling Npgsql accepts) starts the host, when the error
    // does not name the setting, or when any text of the failure quotes the password, the user or the host.
    [Theory]
    [InlineData("No Reset On Close=true")]
    [InlineData("NoResetOnClose=true")]
    [InlineData("no reset on close=True")]
    public void AConnectionStringWithNoResetOnCloseStopsTheHostAndTheErrorNamesOnlyTheSetting(string setting)
    {
        using var factory = new ApiHostFactory().WithDatabase(ConnectionString(setting));

        var message = StartupFailure.AssertOptionsInvalidWithout(factory, "No Reset On Close", Password, User, Host);

        Assert.Contains("ConnectionStrings:Database", message, StringComparison.Ordinal);
    }

    // Breaks when the check refuses the setting even when it is off (the setting is legal then), or refuses a string
    // that does not have it.
    [Theory]
    [InlineData("No Reset On Close=false")]
    [InlineData("Maximum Pool Size=5")]
    [InlineData("Options=-c statement_timeout=5000")]
    [InlineData("Options=-c application_name=search_path_app")]
    public async Task AConnectionStringWithTheSettingOffOrAbsentStartsTheHost(string setting)
    {
        using var factory = new ApiHostFactory().WithDatabase(ConnectionString(setting));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(SecurityRequests.Version, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static NpgsqlDataSource HandMadeDataSource() =>
        new NpgsqlDataSourceBuilder(ConnectionString("No Reset On Close=true")).Build();

    // Breaks when a data source registered by hand with No Reset On Close is accepted by a tenant context when the context is
    // first used (the failure must come before any connection: nothing listens on the host), or when the message quotes a
    // secret.
    [Fact]
    public void ATenantContextRefusesAHandRegisteredDataSourceWithNoResetOnClose()
    {
        var services = new ServiceCollection();
        services.AddSingleton(HandMadeDataSource());
        services.AddTenantDbContext<AccessContext>("Access");
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AccessContext>());

        Assert.Contains("No Reset On Close", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(User, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Host, error.ToString(), StringComparison.Ordinal);
    }

    // Breaks when the migrator accepts the same data source.
    [Fact]
    public void TheMigratorRefusesAHandRegisteredDataSourceWithNoResetOnClose()
    {
        var services = new ServiceCollection();
        services.AddSingleton(HandMadeDataSource());
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ITenantMigrator>());

        Assert.Contains("No Reset On Close", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, error.ToString(), StringComparison.Ordinal);
    }

    // Breaks when the check is made on the string instead of the data source's final settings, or when it rejects a
    // data source that resets its connections.
    [Fact]
    public void ADataSourceThatResetsItsConnectionsIsAccepted()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new NpgsqlDataSourceBuilder(ConnectionString("No Reset On Close=false")).Build());
        services.AddTenantDbContext<AccessContext>("Access");
        services.AddTenantMigrator();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<AccessContext>());
        Assert.NotNull(provider.GetRequiredService<ITenantMigrator>());
    }
}
