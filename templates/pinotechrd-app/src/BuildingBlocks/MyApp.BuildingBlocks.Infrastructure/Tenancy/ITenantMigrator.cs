namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Brings tenant schemas up to date: creates the schema when it is missing, then applies each module's pending
/// migrations in a fixed order (the module <c>Access</c> first, then by name, ordinal). A run on one tenant holds a
/// PostgreSQL advisory lock for that tenant, so concurrent runs on the same tenant take turns and each migration is
/// applied once; a run with nothing pending applies nothing. Registered by
/// <see cref="TenantPersistenceRegistration.AddTenantMigrator"/>; it never runs at API start.
/// </summary>
public interface ITenantMigrator
{
    /// <summary>Migrates one tenant. A failure is reported in the result, not thrown.</summary>
    /// <param name="tenantId">The tenant's id.</param>
    /// <param name="cancellationToken">Stops the run; a cancellation is thrown, not reported.</param>
    /// <returns>What was done for the tenant.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is <see cref="Guid.Empty"/>.</exception>
    Task<TenantMigrationResult> MigrateAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Migrates several tenants, at most <paramref name="maxDegreeOfParallelism"/> at a time. A tenant that fails does
    /// not stop the others: every tenant gets a result, in the order of <paramref name="tenantIds"/>. Each tenant in
    /// progress holds two connections of the pool (its lock and its migration).
    /// </summary>
    /// <param name="tenantIds">The tenants' ids; a repeated id is migrated once and reported at its first position.</param>
    /// <param name="maxDegreeOfParallelism">How many tenants run at once, at least 1.</param>
    /// <param name="cancellationToken">Stops the run; a cancellation is thrown, not reported.</param>
    /// <returns>One result per distinct tenant.</returns>
    Task<IReadOnlyList<TenantMigrationResult>> MigrateAsync(
        IEnumerable<Guid> tenantIds, int maxDegreeOfParallelism, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of migrating one tenant.</summary>
/// <param name="TenantId">The tenant's id.</param>
/// <param name="SchemaName">The tenant's schema.</param>
/// <param name="SchemaCreated">Whether this run created the schema.</param>
/// <param name="Modules">The modules migrated, in the order they ran, up to and including a module that failed.</param>
/// <param name="Error">The failure that stopped the tenant's run, or <see langword="null"/> when it succeeded.</param>
public sealed record TenantMigrationResult(
    Guid TenantId,
    string SchemaName,
    bool SchemaCreated,
    IReadOnlyList<ModuleMigrationResult> Modules,
    Exception? Error)
{
    /// <summary>Gets a value indicating whether every module of the tenant is up to date.</summary>
    public bool Succeeded => Error is null;
}

/// <summary>The outcome of migrating one module of one tenant.</summary>
/// <param name="ModuleName">The module name.</param>
/// <param name="AppliedMigrations">The ids of the migrations this run applied, in order; empty when none was pending.</param>
/// <param name="Succeeded">Whether the module is up to date; <see langword="false"/> for the module that stopped the run.</param>
public sealed record ModuleMigrationResult(string ModuleName, IReadOnlyList<string> AppliedMigrations, bool Succeeded);
