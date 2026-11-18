using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantModuleMigrations.Modules.Billing;

public sealed class Invoice
{
    public long Id { get; set; }
    public string Number { get; set; } = "";
    public decimal Amount { get; set; }
    public DateOnly? DueOn { get; set; }
}

public sealed class Payment
{
    public long Id { get; set; }
    public long InvoiceId { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>The Billing module owns only its own tables. No schema is set: the connection decides it.</summary>
public sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public const string HistoryTable = "__ef_migrations_billing";

    public DbSet<Invoice> Invoices => Set<Invoice>();
    public DbSet<Payment> Payments => Set<Payment>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Invoice>().ToTable("billing_invoices");
        modelBuilder.Entity<Payment>().ToTable("billing_payments").HasIndex(p => p.InvoiceId);
    }

    public static BillingDbContext Create(string connectionString, string schema, SchemaMode mode = SchemaMode.ConnectionString) =>
        new(Tenancy.Options<BillingDbContext>(connectionString, schema, HistoryTable, mode));
}

public sealed class BillingDbContextFactory : IDesignTimeDbContextFactory<BillingDbContext>
{
    public BillingDbContext CreateDbContext(string[] args) => BillingDbContext.Create("Host=localhost", "design_time");
}
