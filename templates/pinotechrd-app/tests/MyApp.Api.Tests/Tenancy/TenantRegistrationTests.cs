using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Api.Tests.Tenancy;

// The registration API: module names, duplicates and the shape of the history table. None of these tests needs Docker.
public class TenantRegistrationTests
{
    // A module registered by hand, as an application that implements IModuleMigrations itself would.
    private sealed class ManualModule(string name) : IModuleMigrations
    {
        public string ModuleName => name;

        public DbContext GetContext(IServiceProvider scopedServices) => throw new NotSupportedException();
    }

    private static NpgsqlDataSource UnusedDataSource() => new NpgsqlDataSourceBuilder(TestDatabase.PlaceholderConnectionString).Build();

    // Breaks when the history table name is not the documented snake case of the module name, or is schema-qualified.
    [Theory]
    [InlineData("Access", "__ef_migrations_access")]
    [InlineData("SalesOrders", "__ef_migrations_sales_orders")]
    [InlineData("HRPayroll", "__ef_migrations_hr_payroll")]
    [InlineData("Sales", "__ef_migrations_sales")]
    public void HistoryTableOfAModuleIsItsSnakeCaseName(string module, string expected)
    {
        var table = TenantPersistenceRegistration.HistoryTableOf(module);

        Assert.Equal(expected, table);
        Assert.DoesNotContain('.', table);
        Assert.StartsWith(TenantPersistenceRegistration.HistoryTablePrefix, table, StringComparison.Ordinal);
    }

    // Breaks when a module name that cannot make a safe, unambiguous table name is accepted.
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("1Sales")]
    [InlineData("Sales Orders")]
    [InlineData("Sales_Orders")]
    [InlineData("Sales.Orders")]
    [InlineData("Ventäs")]
    [InlineData("Sales\"")]
    public void HistoryTableOfRefusesAnInvalidModuleName(string module)
    {
        Assert.ThrowsAny<ArgumentException>(() => TenantPersistenceRegistration.HistoryTableOf(module));
    }

    // Breaks when a module name that would make a table name beyond 63 characters (silently truncated by PostgreSQL, so
    // two modules could share a table) is accepted.
    [Fact]
    public void HistoryTableOfRefusesANameThatMakesATableLongerThan63Characters()
    {
        var longest = "A" + new string('b', 63 - TenantPersistenceRegistration.HistoryTablePrefix.Length - 1);
        Assert.Equal(63, TenantPersistenceRegistration.HistoryTableOf(longest).Length);

        Assert.ThrowsAny<ArgumentException>(() => TenantPersistenceRegistration.HistoryTableOf(longest + "b"));
    }

    // Breaks when registering the same context for the same module twice adds a second module (the migrator would
    // refuse it, or migrate twice).
    [Fact]
    public void RegisteringTheSameContextAndModuleTwiceHasNoFurtherEffect()
    {
        var services = new ServiceCollection();
        services.AddTenantDbContext<SalesContext>("Sales");
        var count = services.Count;

        services.AddTenantDbContext<SalesContext>("Sales");

        Assert.Equal(count, services.Count);
        Assert.Single(services, d => d.ServiceType == typeof(IModuleMigrations));
    }

    // Breaks when two contexts can claim one module name, including a name that differs by case only (they would share
    // one history table, __ef_migrations_sales).
    [Theory]
    [InlineData("Sales")]
    [InlineData("SALES")]
    [InlineData("sales")]
    public void AnotherContextForTheSameModuleNameIsRefusedAtRegistration(string second)
    {
        var services = new ServiceCollection();
        services.AddTenantDbContext<SalesContext>("Sales");

        Assert.Throws<InvalidOperationException>(() => services.AddTenantDbContext<BillingContext>(second));
    }

    // Breaks when one context can serve two modules (each would migrate it under a history table of its own).
    [Fact]
    public void TheSameContextForAnotherModuleIsRefused()
    {
        var services = new ServiceCollection();
        services.AddTenantDbContext<SalesContext>("Sales");

        Assert.Throws<InvalidOperationException>(() => services.AddTenantDbContext<SalesContext>("Billing"));
    }

    // Breaks when the migrator accepts two modules of the same name registered by hand.
    [Fact]
    public void TheMigratorRefusesTwoModulesWithTheSameName()
    {
        var services = new ServiceCollection();
        services.AddSingleton(UnusedDataSource());
        services.AddSingleton<IModuleMigrations>(new ManualModule("Sales"));
        services.AddSingleton<IModuleMigrations>(new ManualModule("Sales"));
        services.AddTenantMigrator();
        using var provider = services.BuildServiceProvider();

        Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ITenantMigrator>());
    }

    // Breaks when two hand-registered modules that differ by case only are accepted by the migrator: they map to one
    // history table (__ef_migrations_sales), so one would hide the other's migrations.
    [Fact]
    public void TheMigratorRefusesTwoModulesWhoseNamesDifferOnlyByCase()
    {
        var services = new ServiceCollection();
        services.AddSingleton(UnusedDataSource());
        services.AddSingleton<IModuleMigrations>(new ManualModule("Sales"));
        services.AddSingleton<IModuleMigrations>(new ManualModule("SALES"));
        services.AddTenantMigrator();
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<ITenantMigrator>());
        Assert.Contains("'Sales'", error.Message, StringComparison.Ordinal);
        Assert.Contains("'SALES'", error.Message, StringComparison.Ordinal);
    }

    // Breaks when AddTenantDbContext does not register the module for the migrator under the name it was given.
    [Fact]
    public void EachContextRegistersItsModuleForTheMigrator()
    {
        var services = new ServiceCollection();
        services.AddTenantDbContext<SalesContext>("Sales");
        services.AddTenantDbContext<AccessContext>("Access");
        using var provider = services.BuildServiceProvider();

        var names = provider.GetServices<IModuleMigrations>().Select(m => m.ModuleName);

        Assert.Equal(["Access", "Sales"], names.Order(StringComparer.Ordinal));
    }
}
