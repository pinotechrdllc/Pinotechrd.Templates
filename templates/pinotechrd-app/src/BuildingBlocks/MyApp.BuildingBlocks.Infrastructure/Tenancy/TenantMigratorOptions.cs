namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>Settings of the tenant migrator (<see cref="TenantPersistenceRegistration.AddTenantMigrator"/>).</summary>
public sealed class TenantMigratorOptions
{
    /// <summary>
    /// Gets or sets the login the application connects with, when the migrator connects with another login (one that owns
    /// the database, with <c>CREATEROLE</c>). The migrator makes this login a member of every tenant's role
    /// (<see cref="TenantRoleName.For"/>) <c>WITH INHERIT FALSE, SET TRUE</c>: it gains no rights of its own, and each
    /// tenant connection assumes its tenant's role. <see langword="null"/> (the default) when the application and the
    /// migrator use one login: that login receives the memberships (ADR 0012).
    /// </summary>
    public string? ApplicationRole { get; set; }
}
