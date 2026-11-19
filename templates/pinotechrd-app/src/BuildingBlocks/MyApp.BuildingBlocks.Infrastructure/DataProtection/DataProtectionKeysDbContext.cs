using Microsoft.EntityFrameworkCore;

namespace MyApp.BuildingBlocks.Infrastructure.DataProtection;

/// <summary>
/// The shared context of the Data Protection key ring: the one table <see cref="Table"/> in the schema
/// <see cref="Schema"/>, shared by every instance of the API so they all protect and unprotect with the same keys.
/// Registered with <c>AddSharedDbContext</c> by <see cref="DataProtectionPersistenceRegistration.AddDataProtectionKeyStore"/>,
/// history table <c>data_protection.__ef_migrations_data_protection</c>, migrated by the migrator like the catalog.
/// </summary>
/// <param name="options">The options built by the shared persistence registration.</param>
public sealed class DataProtectionKeysDbContext(DbContextOptions<DataProtectionKeysDbContext> options) : DbContext(options)
{
    /// <summary>The schema of the key ring.</summary>
    public const string Schema = "data_protection";

    /// <summary>The table of the key ring.</summary>
    public const string Table = "keys";

    /// <summary>Gets the elements of the key ring.</summary>
    public DbSet<DataProtectionKey> Keys => Set<DataProtectionKey>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<DataProtectionKey>(entity =>
        {
            entity.ToTable(Table);
            entity.Property(e => e.Id).UseIdentityAlwaysColumn();
            entity.Property(e => e.Xml).IsRequired();
        });
    }
}
