using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace MyApp.Api.Tests.Tenancy;

// Fixture modules for the tenant persistence tests: contexts with two hand-written migrations each. They live in the
// test project only, so nothing here reaches the real host or the committed contract. No model names a schema (except
// the two contexts made to prove that the migrator refuses it).

/// <summary>The one entity of every fixture module, mapped to the module's own table.</summary>
public sealed class FixtureItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Name { get; set; } = string.Empty;

    public string? CreatedBy { get; set; }
}

/// <summary>A fixture context. Migrations are written by hand, so the pending-model-changes check is off (as the README says).</summary>
public abstract class FixtureContext(DbContextOptions options, string table) : DbContext(options)
{
    public DbSet<FixtureItem> Items => Set<FixtureItem>();

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<FixtureItem>(e =>
        {
            e.ToTable(table);
            e.HasKey(i => i.Id);
        });
}

public sealed class AccessContext(DbContextOptions<AccessContext> options) : FixtureContext(options, "access_items");

public sealed class BillingContext(DbContextOptions<BillingContext> options) : FixtureContext(options, "billing_items");

public sealed class SalesContext(DbContextOptions<SalesContext> options) : FixtureContext(options, "sales_items");

/// <summary>A context with no entity and no migration, for the tests of the order of modules.</summary>
public abstract class EmptyContext(DbContextOptions options) : DbContext(options);

public sealed class AaaContext(DbContextOptions<AaaContext> options) : EmptyContext(options);

public sealed class ZuluContext(DbContextOptions<ZuluContext> options) : EmptyContext(options);

public sealed class AlphaContext(DbContextOptions<AlphaContext> options) : EmptyContext(options);

/// <summary>A context whose model names a default schema: a tenant context must not.</summary>
public sealed class DefaultSchemaContext(DbContextOptions<DefaultSchemaContext> options) : DbContext(options)
{
    public const string Schema = "custom_default_schema";

    public DbSet<FixtureItem> Items => Set<FixtureItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<FixtureItem>().ToTable("default_schema_items");
    }
}

/// <summary>A context whose table names a schema: a tenant context must not.</summary>
public sealed class TableSchemaContext(DbContextOptions<TableSchemaContext> options) : DbContext(options)
{
    public const string Schema = "custom_table_schema";

    public DbSet<FixtureItem> Items => Set<FixtureItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<FixtureItem>().ToTable("table_schema_items", Schema);
}

/// <summary>
/// A context whose only migration waits for an advisory lock that the test holds, so a migration can be kept in progress
/// for as long as the test wants.
/// </summary>
public sealed class GateContext(DbContextOptions<GateContext> options) : DbContext(options)
{
    /// <summary>The key of the advisory lock the migration waits for.</summary>
    public const long GateKey = 7281961;
}

public abstract class CreateItemsTable(string table) : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.CreateTable(
            name: table,
            columns: t => new
            {
                id = t.Column<Guid>(type: "uuid", nullable: false),
                name = t.Column<string>(type: "text", nullable: false),
            },
            constraints: t => t.PrimaryKey("pk_" + table, x => x.id));

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable(table);
}

public abstract class AddCreatedByColumn(string table) : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<string>(name: "created_by", table: table, type: "text", nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropColumn("created_by", table);
}

[DbContext(typeof(AccessContext))]
[Migration(AccessItems.Id)]
public sealed class AccessItems() : CreateItemsTable("access_items")
{
    public const string Id = "20260101000001_AccessItems";
}

[DbContext(typeof(AccessContext))]
[Migration(AccessCreatedBy.Id)]
public sealed class AccessCreatedBy() : AddCreatedByColumn("access_items")
{
    public const string Id = "20260101000002_AccessCreatedBy";
}

[DbContext(typeof(BillingContext))]
[Migration(BillingItems.Id)]
public sealed class BillingItems() : CreateItemsTable("billing_items")
{
    public const string Id = "20260101000001_BillingItems";
}

[DbContext(typeof(BillingContext))]
[Migration(BillingCreatedBy.Id)]
public sealed class BillingCreatedBy() : AddCreatedByColumn("billing_items")
{
    public const string Id = "20260101000002_BillingCreatedBy";
}

[DbContext(typeof(SalesContext))]
[Migration(SalesItems.Id)]
public sealed class SalesItems() : CreateItemsTable("sales_items")
{
    public const string Id = "20260101000001_SalesItems";
}

[DbContext(typeof(SalesContext))]
[Migration(SalesCreatedBy.Id)]
public sealed class SalesCreatedBy() : AddCreatedByColumn("sales_items")
{
    public const string Id = "20260101000002_SalesCreatedBy";
}

[DbContext(typeof(GateContext))]
[Migration(GateWait.Id)]
public sealed class GateWait : Migration
{
    public const string Id = "20260101000001_GateWait";

    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("SELECT pg_advisory_xact_lock(" + GateContext.GateKey + ")");
}
