namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>Settings of the tenant migrator (<see cref="TenantPersistenceRegistration.AddTenantMigrator"/>).</summary>
public sealed class TenantMigratorOptions
{
    /// <summary>
    /// Gets or sets the database role the application connects with, when the migrator connects with another role (a
    /// role that owns the schemas). The migrator then grants this role <c>USAGE</c> on the shared extensions schema, and
    /// nothing more: privileges on tenant schemas and tables for a separate application role are not managed yet.
    /// <see langword="null"/> (the default) when the application and the migrator use one role, as the template does.
    /// </summary>
    public string? ApplicationRole { get; set; }
}
