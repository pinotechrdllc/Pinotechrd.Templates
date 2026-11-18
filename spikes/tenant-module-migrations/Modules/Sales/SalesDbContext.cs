using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantModuleMigrations.Modules.Sales;

public sealed class Order
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public long CustomerId { get; set; }
    public decimal Total { get; set; }
}

public sealed class Customer
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>The Sales module owns only its own tables. No schema is set: the connection decides it.</summary>
public sealed class SalesDbContext(DbContextOptions<SalesDbContext> options) : DbContext(options)
{
    public const string HistoryTable = "__ef_migrations_sales";

    public DbSet<Order> Orders => Set<Order>();
    public DbSet<Customer> Customers => Set<Customer>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Order>().ToTable("sales_orders").HasIndex(o => o.Number).IsUnique();
        modelBuilder.Entity<Customer>().ToTable("sales_customers");
    }

    public static SalesDbContext Create(string connectionString, string schema, SchemaMode mode = SchemaMode.ConnectionString) =>
        new(Tenancy.Options<SalesDbContext>(connectionString, schema, HistoryTable, mode));
}

public sealed class SalesDbContextFactory : IDesignTimeDbContextFactory<SalesDbContext>
{
    public SalesDbContext CreateDbContext(string[] args) => SalesDbContext.Create("Host=localhost", "design_time");
}
