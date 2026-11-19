using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.Api.Tests.Tenancy;

/// <summary>The referenced side of a foreign key: a table that the tenant context maps but does not migrate.</summary>
public sealed class GuardTarget
{
    public Guid Id { get; set; } = Guid.NewGuid();
}

/// <summary>The migrated side: a table with a foreign key to <see cref="GuardTarget"/>.</summary>
public sealed class GuardDependent
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TargetId { get; set; }

    public GuardTarget Target { get; set; } = null!;
}

/// <summary>A migrated table that holds a foreign key to an excluded table of the shared catalog schema.</summary>
public sealed class FkToCatalogContext(DbContextOptions<FkToCatalogContext> options) : DbContext(options)
{
    public const string Dependent = "guard_catalog_dependents";

    public const string Target = "guard_catalog_targets";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuardTarget>(e => e.ToTable(Target, "catalog", t => t.ExcludeFromMigrations()));
        modelBuilder.Entity<GuardDependent>(e => e.ToTable(Dependent));
    }
}

/// <summary>A migrated table that holds a foreign key to an excluded table mapped into another tenant's schema.</summary>
public sealed class FkToOtherTenantContext(DbContextOptions<FkToOtherTenantContext> options) : DbContext(options)
{
    public const string Dependent = "guard_tenant_dependents";

    public const string Target = "guard_tenant_targets";

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<GuardTarget>(e => e.ToTable(Target, TenantSchemaName.For(Guid.NewGuid()), t => t.ExcludeFromMigrations()));
        modelBuilder.Entity<GuardDependent>(e => e.ToTable(Dependent));
    }
}

public enum GuardMood
{
    Happy,
    Sad,
}

public sealed class GuardThing
{
    public int Id { get; set; }

    public GuardMood Mood { get; set; }
}

/// <summary>A shared context that asks for a PostgreSQL extension, which is database-wide.</summary>
public sealed class SharedWithExtensionContext(DbContextOptions<SharedWithExtensionContext> options) : DbContext(options)
{
    public const string Schema = "guard_ext";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresExtension("citext");
        modelBuilder.Entity<GuardThing>(e => e.Ignore(t => t.Mood));
    }
}

/// <summary>A shared context whose enum type sits in the public schema.</summary>
public sealed class SharedWithForeignEnumContext(DbContextOptions<SharedWithForeignEnumContext> options) : DbContext(options)
{
    public const string Schema = "guard_enum";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasPostgresEnum<GuardMood>(schema: "public");
        modelBuilder.Entity<GuardThing>();
    }
}

/// <summary>A shared context with a sequence in a schema that is not its own.</summary>
public sealed class SharedWithForeignSequenceContext(DbContextOptions<SharedWithForeignSequenceContext> options) : DbContext(options)
{
    public const string Schema = "guard_seq";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.HasSequence("guard_foreign_seq", "guard_seq_elsewhere");
        modelBuilder.Entity<GuardThing>(e => e.Ignore(t => t.Mood));
    }
}

// What the migrators refuse before they create or change anything: a tenant model whose foreign key leaves the tenant's
// schema, and a shared model with an object that is not in its schema.
[Collection(PostgresContainer.Name)]
public class MigratorModelGuardTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // Breaks when a migrated tenant table may keep a foreign key to an excluded table of the catalog: the tenant's tables
    // would depend on rows outside its schema.
    [Fact]
    public async Task ATenantTableWithAForeignKeyToAnExcludedCatalogTableIsRefusedAndNamesTheTable()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<FkToCatalogContext>("GuardCatalog");
                services.AddTenantMigrator();
            });
        var tenant = Guid.NewGuid();

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenant, Token);

        Assert.False(result.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Contains("GuardCatalog", error.Message, StringComparison.Ordinal);
        Assert.True(
            error.Message.Contains(FkToCatalogContext.Dependent, StringComparison.Ordinal)
            || error.Message.Contains(FkToCatalogContext.Target, StringComparison.Ordinal),
            error.Message);
        Assert.DoesNotContain(
            await rig.UserTablesAsync(), t => t.EndsWith(FkToCatalogContext.Dependent, StringComparison.Ordinal));
    }

    // Breaks when the foreign key check only looks at the catalog and lets a key into the schema of another tenant through.
    [Fact]
    public async Task ATenantTableWithAForeignKeyToAnExcludedTableOfAnotherTenantSchemaIsRefusedAndNamesTheTable()
    {
        await using var rig = await TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                services.AddTenantDbContext<FkToOtherTenantContext>("GuardOther");
                services.AddTenantMigrator();
            });

        var result = await rig.Services.GetRequiredService<ITenantMigrator>().MigrateAsync(Guid.NewGuid(), Token);

        Assert.False(result.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(result.Error);
        Assert.Contains("GuardOther", error.Message, StringComparison.Ordinal);
        Assert.True(
            error.Message.Contains(FkToOtherTenantContext.Dependent, StringComparison.Ordinal)
            || error.Message.Contains(FkToOtherTenantContext.Target, StringComparison.Ordinal),
            error.Message);
        Assert.DoesNotContain(
            await rig.UserTablesAsync(), t => t.EndsWith(FkToOtherTenantContext.Dependent, StringComparison.Ordinal));
    }

    // Breaks when a shared model with a PostgreSQL extension is migrated: an extension is created database-wide, outside
    // the schema the context owns.
    [Fact]
    public async Task ASharedModelWithAPostgresExtensionIsRefusedAndNothingIsCreated()
    {
        await using var rig = await StartSharedAsync(
            services => services.AddSharedDbContext<SharedWithExtensionContext>(SharedWithExtensionContext.Schema));

        var result = await rig.Services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(1, Token);

        var shared = Assert.Single(result.Shared);
        Assert.False(shared.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(shared.Error);
        Assert.Contains("citext", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0L, await rig.ScalarAsync<long>("SELECT count(*) FROM pg_extension WHERE extname = 'citext'"));
        Assert.False(await rig.SchemaExistsAsync(SharedWithExtensionContext.Schema) && (await rig.UserTablesAsync()).Count > 0);
    }

    // Breaks when a shared model with an enum type in another schema is migrated.
    [Fact]
    public async Task ASharedModelWithAnEnumOutsideItsSchemaIsRefusedAndNothingIsCreated()
    {
        await using var rig = await StartSharedAsync(
            services => services.AddSharedDbContext<SharedWithForeignEnumContext>(SharedWithForeignEnumContext.Schema));

        var result = await rig.Services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(1, Token);

        var shared = Assert.Single(result.Shared);
        Assert.False(shared.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(shared.Error);
        Assert.Contains("guard_mood", error.Message, StringComparison.Ordinal);
        Assert.Equal(
            0L,
            await rig.ScalarAsync<long>("SELECT count(*) FROM pg_type t JOIN pg_namespace n ON n.oid = t.typnamespace WHERE t.typtype = 'e'"));
    }

    // Breaks when a shared model with a sequence in another schema is migrated.
    [Fact]
    public async Task ASharedModelWithASequenceOutsideItsSchemaIsRefusedAndNothingIsCreated()
    {
        await using var rig = await StartSharedAsync(
            services => services.AddSharedDbContext<SharedWithForeignSequenceContext>(SharedWithForeignSequenceContext.Schema));

        var result = await rig.Services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(1, Token);

        var shared = Assert.Single(result.Shared);
        Assert.False(shared.Succeeded);
        var error = Assert.IsType<InvalidOperationException>(shared.Error);
        Assert.Contains("guard_foreign_seq", error.Message, StringComparison.Ordinal);
        Assert.False(await rig.SchemaExistsAsync("guard_seq_elsewhere"));
    }

    private Task<TenantRig> StartSharedAsync(Action<IServiceCollection> addShared) =>
        TenantRig.StartAsync(
            postgres,
            register: services =>
            {
                addShared(services);
                services.AddDatabaseMigrator();
            });
}
