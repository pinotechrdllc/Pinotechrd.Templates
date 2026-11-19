using Microsoft.Extensions.Configuration;
using MyApp.Api.Tests.Database;
using MyApp.Migrator.Tests.Support;

namespace MyApp.Migrator.Tests.Bootstrap;

// The administrator's password and where it may come from: at least 15 Unicode scalar values, from the environment or user
// secrets only, and never echoed. Settings of the command are refused without it. Real PostgreSQL for the command runs.
[Collection(PostgresContainer.Name)]
public class BootstrapPasswordTests(PostgresFixture postgres)
{
    private static async Task AssertNothingWrittenAsync(string database)
    {
        Assert.Empty(await BootstrapSupport.OperatorTenantsAsync(database));
        Assert.Equal(0, await BootstrapSupport.OperatorSubscriptionCountAsync(database));
        Assert.Equal(0, await Provisioning.ProvisionSupport.TenantSchemaCountAsync(database));
    }

    // Breaks when the minimum counts UTF-16 characters instead of Unicode scalar values (14 emoji are 28 characters), accepts
    // 14 runes, writes anything before refusing, or quotes the password or the email in the report or on standard error.
    [Theory]
    [InlineData(14, false)]
    [InlineData(14, true)]
    [InlineData(1, false)]
    [InlineData(0, false)]
    public async Task APasswordOfFewerThanFifteenRunesExitsTwoWritesNothingAndIsNeverEchoed(int runes, bool emoji)
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var password = BootstrapRun.PasswordOfRunes(runes, emoji);

        var run = await BootstrapRun.ExecuteAsync(database, password);

        Assert.Equal(MigratorHost.InvalidSettings, run.ExitCode);
        Assert.Contains("AdminPassword", run.Report, StringComparison.Ordinal);
        if (password.Length > 0)
        {
            Assert.DoesNotContain(password, run.Everything, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(BootstrapRun.DefaultEmail, run.Everything, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(database);
    }

    // Breaks when the boundary moves: exactly 15 runes must be accepted, ASCII or not, and the password never echoed.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APasswordOfExactlyFifteenRunesIsAccepted(bool emoji)
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var password = BootstrapRun.PasswordOfRunes(15, emoji);

        var run = await BootstrapRun.ExecuteAsync(database, password);

        Assert.True(run.ExitCode == 0, run.Report);
        var tenant = await BootstrapSupport.TenantAsync(database);
        Assert.True(BootstrapRun.Verifies(await BootstrapSupport.PasswordHashAsync(database, tenant), password));
        Assert.DoesNotContain(password, run.Everything, StringComparison.Ordinal);
    }

    // Breaks when there is no upper bound (a very long password is hashed), or the bound is off by one.
    [Theory]
    [InlineData(256, 0)]
    [InlineData(257, MigratorHost.InvalidSettings)]
    public async Task ThePasswordHasAnUpperBoundOf256Runes(int runes, int expectedExit)
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var password = BootstrapRun.PasswordOfRunes(runes, emoji: false);

        var run = await BootstrapRun.ExecuteAsync(database, password);

        Assert.True(run.ExitCode == expectedExit, run.Report);
        Assert.DoesNotContain(password, run.Everything, StringComparison.Ordinal);
        if (expectedExit != 0)
        {
            await AssertNothingWrittenAsync(database);
        }
    }

    // Breaks when the password is accepted from the command line (visible in the process list and shell history), is echoed
    // when refused, or the refusal comes after something was written.
    [Fact]
    public async Task APasswordOnTheCommandLineExitsTwoWritesNothingAndIsNeverEchoed()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);
        var password = "from-the-command-line-1234";

        var run = await BootstrapRun.ExecuteAsync(database, password: null, $"--Bootstrap:AdminPassword={password}");

        Assert.Equal(MigratorHost.InvalidSettings, run.ExitCode);
        Assert.Contains("AdminPassword", run.Report, StringComparison.Ordinal);
        Assert.DoesNotContain(password, run.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapRun.DefaultEmail, run.Everything, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(database);
    }

    // Breaks when the command line wins quietly over a good environment password, or the refusal depends on the other source.
    [Fact]
    public async Task APasswordOnTheCommandLineIsRefusedEvenWhenTheEnvironmentHasOne()
    {
        var database = await BootstrapSupport.MigratedDatabaseAsync(postgres);

        var run = await BootstrapRun.ExecuteAsync(database, BootstrapRun.GoodPassword, "--Bootstrap:AdminPassword=on-the-command-line-99");

        Assert.Equal(MigratorHost.InvalidSettings, run.ExitCode);
        Assert.DoesNotContain("on-the-command-line-99", run.Everything, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapRun.GoodPassword, run.Everything, StringComparison.Ordinal);
        await AssertNothingWrittenAsync(database);
    }

    // Breaks when the source check misses a settings file whose name starts with appsettings (appsettings.json, any
    // environment's file) or flags a source that is allowed: the environment, an in-memory source standing for user secrets,
    // and a json file of another name.
    [Theory]
    [InlineData("appsettings.json", true)]
    [InlineData("appsettings.Production.json", true)]
    [InlineData("APPSETTINGS.Development.json", true)]
    [InlineData("secrets.json", false)]
    public void ThePasswordSourceFailureRefusesAnyAppsettingsFileAndNothingElseAndNeverQuotesTheValue(string fileName, bool refused)
    {
        var directory = Directory.CreateTempSubdirectory("bootstrap-source-");
        try
        {
            var path = Path.Combine(directory.FullName, fileName);
            File.WriteAllText(path, "{ \"Bootstrap\": { \"AdminPassword\": \"in-a-file-secret-value-1\" } }");
            var configuration = new ConfigurationBuilder().AddJsonFile(path, optional: false).Build();

            var failure = BootstrapOptions.PasswordSourceFailure(configuration);

            Assert.Equal(refused, failure is not null);
            if (failure is not null)
            {
                Assert.Contains("AdminPassword", failure, StringComparison.Ordinal);
                Assert.DoesNotContain("in-a-file-secret-value-1", failure, StringComparison.Ordinal);
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Breaks when an appsettings file without the password is refused (only a password in it is the problem), or when the
    // command line without the password is.
    [Fact]
    public void ThePasswordSourceFailureIsNullWhenNoRefusedSourceHoldsThePassword()
    {
        var directory = Directory.CreateTempSubdirectory("bootstrap-source-");
        try
        {
            var path = Path.Combine(directory.FullName, "appsettings.json");
            File.WriteAllText(path, "{ \"Bootstrap\": { \"Slug\": \"ops\" } }");
            var configuration = new ConfigurationBuilder()
                .AddJsonFile(path, optional: false)
                .AddCommandLine(["--Bootstrap:Name=Ops"])
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Bootstrap:AdminPassword"] = "from-user-secrets-or-env-1" })
                .Build();

            Assert.Null(BootstrapOptions.PasswordSourceFailure(configuration));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    // Breaks when the check ignores the command line source.
    [Fact]
    public void ThePasswordSourceFailureRefusesThePasswordOnTheCommandLine()
    {
        var configuration = new ConfigurationBuilder().AddCommandLine(["--Bootstrap:AdminPassword=line-secret-value-1234"]).Build();

        var failure = BootstrapOptions.PasswordSourceFailure(configuration);

        Assert.NotNull(failure);
        Assert.DoesNotContain("line-secret-value-1234", failure, StringComparison.Ordinal);
    }

    // Breaks when the password is accepted by the environment only through a case or separator the check does not look at.
    [Fact]
    public void ThePasswordSourceFailureIsNullForTheEnvironmentVariableSource()
    {
        var name = "Bootstrap__AdminPassword";
        var previous = Environment.GetEnvironmentVariable(name);
        try
        {
            Environment.SetEnvironmentVariable(name, "from-the-environment-1234");
            var configuration = new ConfigurationBuilder().AddEnvironmentVariables().Build();

            Assert.Equal("from-the-environment-1234", configuration["Bootstrap:AdminPassword"]);
            Assert.Null(BootstrapOptions.PasswordSourceFailure(configuration));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    // Breaks when Bootstrap settings given without the command are ignored and the migrator migrates (as Provision settings
    // are refused: ADR 0011), or when the refusal echoes a value.
    [Theory]
    [InlineData("--Bootstrap:Slug=ops")]
    [InlineData("--Bootstrap:AdminEmail=root.admin@example.test")]
    public async Task BootstrapSettingsOnTheCommandLineWithoutTheCommandExitTwoAndMigrateNothing(string setting)
    {
        var database = await postgres.CreateDatabaseAsync();

        var run = await MigratorRun.ExecuteAsync(database, setting);

        Assert.Equal(MigratorHost.InvalidSettings, run.ExitCode);
        Assert.Contains("Bootstrap", run.Report, StringComparison.Ordinal);
        Assert.DoesNotContain("root.admin@example.test", run.Report, StringComparison.Ordinal);
        Assert.False(await Db.SchemaExistsAsync(database, "catalog"));
    }

    // Breaks when the environment's Bootstrap__AdminPassword is ignored without the command (the settings are read from any
    // source), or when the password is echoed in the refusal.
    [Fact]
    public async Task ABootstrapPasswordInTheEnvironmentWithoutTheCommandExitsTwoAndIsNeverEchoed()
    {
        var database = await postgres.CreateDatabaseAsync();

        var runs = await BootstrapRun.ExecuteAllAsync(BootstrapRun.GoodPassword, [[$"--{MigratorRun.ConnectionKey}={database}"]]);

        var run = Assert.Single(runs);
        Assert.Equal(MigratorHost.InvalidSettings, run.ExitCode);
        Assert.Contains("Bootstrap", run.Report, StringComparison.Ordinal);
        Assert.DoesNotContain(BootstrapRun.GoodPassword, run.Everything, StringComparison.Ordinal);
        Assert.False(await Db.SchemaExistsAsync(database, "catalog"));
    }
}
