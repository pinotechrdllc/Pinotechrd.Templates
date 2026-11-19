using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Tenancy;

/// <summary>A row of a table that lives in a shared schema and that the context does not migrate.</summary>
public sealed class SharedProbe
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

/// <summary>A tenant context whose only table sits in a shared schema and is excluded from migrations.</summary>
public sealed class ExcludedOnlyContext(DbContextOptions<ExcludedOnlyContext> options) : DbContext(options)
{
    public const string SharedSchema = "excluded_only_shared";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<SharedProbe>(e =>
        {
            e.ToTable("shared_probe", SharedSchema, t => t.ExcludeFromMigrations());
            e.HasKey(p => p.Id);
        });
}

/// <summary>An excluded table in a shared schema next to a migrated table with no schema.</summary>
public sealed class ExcludedAndMigratedContext(DbContextOptions<ExcludedAndMigratedContext> options) : DbContext(options)
{
    public const string SharedSchema = "excluded_mixed_shared";

    public const string Table = "mixed_items";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SharedProbe>(e =>
        {
            e.ToTable("shared_probe", SharedSchema, t => t.ExcludeFromMigrations());
            e.HasKey(p => p.Id);
        });
        modelBuilder.Entity<FixtureItem>(e =>
        {
            e.ToTable(Table);
            e.HasKey(i => i.Id);
            e.Ignore(i => i.CreatedBy);
        });
    }
}

[DbContext(typeof(ExcludedAndMigratedContext))]
[Migration(MixedItems.Id)]
public sealed class MixedItems() : CreateItemsTable(ExcludedAndMigratedContext.Table)
{
    public const string Id = "20260101000001_MixedItems";
}

/// <summary>An excluded table in a shared schema next to a migrated table that names a schema: still refused.</summary>
public sealed class ExcludedAndMigratedWithSchemaContext(DbContextOptions<ExcludedAndMigratedWithSchemaContext> options) : DbContext(options)
{
    public const string MigratedSchema = "excluded_mixed_bad_schema";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SharedProbe>(e =>
        {
            e.ToTable("shared_probe", "excluded_mixed_bad_shared", t => t.ExcludeFromMigrations());
            e.HasKey(p => p.Id);
        });
        modelBuilder.Entity<FixtureItem>(e =>
        {
            e.ToTable("bad_items", MigratedSchema);
            e.HasKey(i => i.Id);
        });
    }
}

/// <summary>An excluded table in a shared schema in a model that also sets a default schema: still refused.</summary>
public sealed class ExcludedWithDefaultSchemaContext(DbContextOptions<ExcludedWithDefaultSchemaContext> options) : DbContext(options)
{
    public const string DefaultSchema = "excluded_default_schema";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(DefaultSchema);
        modelBuilder.Entity<SharedProbe>(e =>
        {
            e.ToTable("shared_probe", "excluded_default_shared", t => t.ExcludeFromMigrations());
            e.HasKey(p => p.Id);
        });
    }
}

// The tenant migrator ignores tables excluded from migrations when it checks that a model names no schema (the Access
// module keeps the shared catalog tables in its model that way), and keeps every other schema check.
[Collection(PostgresContainer.Name)]
public class TenantMigratorExcludedTableTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when a model whose only schema is on a table excluded from migrations is refused again, or when the migrator
    // creates the shared schema of that table.
    [Fact]
    public async Task AContextWhoseOnlySchemaIsOnAnExcludedTableIsMigrated()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<ExcludedOnlyContext>("ExcludedOnly");
                services.AddTenantMigrator();
            });
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.True(await rig.SchemaExistsAsync(TenantSchemaName.For(tenant)));
        Assert.False(await rig.SchemaExistsAsync(ExcludedOnlyContext.SharedSchema));
    }

    // Breaks when the excluded table's schema leaks into the tenant's migration: the migrated table must land in the
    // tenant's schema, its history in the tenant's schema, and nothing in the shared schema.
    [Fact]
    public async Task AMigratedTableNextToAnExcludedSharedTableLandsInTheTenantSchemaOnly()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<ExcludedAndMigratedContext>("ExcludedMixed");
                services.AddTenantMigrator();
            });
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.True(result.Succeeded, result.Error?.ToString());
        Assert.Equal([MixedItems.Id], await rig.HistoryAsync(tenant, "ExcludedMixed"));
        var tables = await rig.UserTablesAsync();
        Assert.Contains($"{TenantSchemaName.For(tenant)}.{ExcludedAndMigratedContext.Table}", tables);
        Assert.DoesNotContain(tables, t => t.StartsWith(ExcludedAndMigratedContext.SharedSchema + ".", StringComparison.Ordinal));
        Assert.False(await rig.SchemaExistsAsync(ExcludedAndMigratedContext.SharedSchema));
    }

    // Breaks when the exemption of excluded tables also exempts a migrated table that names a schema.
    [Fact]
    public async Task AMigratedTableThatNamesASchemaIsStillRefusedNextToAnExcludedTable()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<ExcludedAndMigratedWithSchemaContext>("ExcludedBad");
                services.AddTenantMigrator();
            });

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.False(result.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Contains("ExcludedBad", error.Message, StringComparison.Ordinal);
        Assert.Contains("schema", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(await rig.SchemaExistsAsync(ExcludedAndMigratedWithSchemaContext.MigratedSchema));
    }

    // Breaks when the exemption of excluded tables also exempts a default schema on the model.
    [Fact]
    public async Task ADefaultSchemaOnTheModelIsStillRefusedNextToAnExcludedTable()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<ExcludedWithDefaultSchemaContext>("ExcludedDefault");
                services.AddTenantMigrator();
            });

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.False(result.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Contains("ExcludedDefault", error.Message, StringComparison.Ordinal);
        Assert.False(await rig.SchemaExistsAsync(ExcludedWithDefaultSchemaContext.DefaultSchema));
    }
}
