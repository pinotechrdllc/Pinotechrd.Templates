using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using MyApp.Infrastructure.Database;
using Npgsql;

namespace MyApp.Api.Tests.Database;

// Criterion 15: the build-time contract generation runs the application without configuration and without a
// database. The tool that does it is not available in-process, so these tests lock the mechanism it relies on: the
// registration with validation at start turned off. (The served document equaling the committed contract is checked by
// ContractIsCurrentTests, whose host has only a placeholder connection string.)
public class ContractGenerationTests
{
    private static HostApplicationBuilder BuilderWithoutConnectionString(bool validateOnStart)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Services.AddDatabase(validateOnStart);
        return builder;
    }

    // Breaks when turning the validation off no longer lets a host start without a connection string, or when the
    // data source stops being registered in that mode (the API description would then differ from the real application).
    [Fact]
    public async Task HostStartsWithoutAConnectionStringWhenValidationAtStartIsOffAndStillRegistersTheDataSource()
    {
        var builder = BuilderWithoutConnectionString(validateOnStart: false);
        var descriptor = Assert.Single(builder.Services, d => d.ServiceType == typeof(NpgsqlDataSource));
        Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
        using var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
    }

    // Breaks when the same registration with validation on lets a host without a connection string start (the
    // default must stay fail fast), or when the error stops naming the setting.
    [Fact]
    public async Task HostDoesNotStartWithoutAConnectionStringWhenValidationAtStartIsOn()
    {
        using var host = BuilderWithoutConnectionString(validateOnStart: true).Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(DatabaseRegistration.ConnectionStringKey, exception.Message, StringComparison.Ordinal);
    }

    // Breaks when skipping the check at start also skips it for use: a data source built from no or an invalid value
    // must refuse, naming the setting and not quoting the value.
    [Theory]
    [InlineData(null)]
    [InlineData("not a connection string Pw-Secret-55aa")]
    public void DataSourceStillRefusesAnInvalidValueWhenItIsResolvedAfterValidationAtStartWasSkipped(string? value)
    {
        var builder = BuilderWithoutConnectionString(validateOnStart: false);
        if (value is not null)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [DatabaseRegistration.ConnectionStringKey] = value });
        }

        using var host = builder.Build();

        var exception = Assert.Throws<OptionsValidationException>(() => host.Services.GetRequiredService<NpgsqlDataSource>());
        Assert.Contains(DatabaseRegistration.ConnectionStringKey, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Pw-Secret-55aa", exception.ToString(), StringComparison.Ordinal);
    }
}
