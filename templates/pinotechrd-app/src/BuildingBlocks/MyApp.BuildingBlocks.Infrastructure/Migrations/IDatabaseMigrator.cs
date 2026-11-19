using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// Brings the whole database up to date: under one global advisory lock (<see cref="DatabaseMigrator.GlobalLockKey"/>),
/// the shared contexts first, in registration order, then every tenant the <see cref="ITenantDirectory"/> lists, through
/// the <see cref="ITenantMigrator"/>. A failed shared context stops the run before any tenant. Registered by
/// <see cref="SharedPersistenceRegistration.AddDatabaseMigrator"/>, only in the migrator command; it never runs at API
/// start.
/// </summary>
public interface IDatabaseMigrator
{
    /// <summary>Runs the migration. A failure of a shared context or a tenant is reported in the result, not thrown.</summary>
    /// <param name="maxDegreeOfParallelism">How many tenants run at once, at least 1.</param>
    /// <param name="cancellationToken">Stops the run; a cancellation is thrown, not reported.</param>
    /// <returns>What was done.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxDegreeOfParallelism"/> is less than 1.</exception>
    Task<DatabaseMigrationResult> MigrateAsync(int maxDegreeOfParallelism, CancellationToken cancellationToken = default);
}

/// <summary>The outcome of a database migration.</summary>
/// <param name="Shared">The shared contexts migrated, in order, up to and including one that failed.</param>
/// <param name="Tenants">The tenants migrated, in the directory's order; empty when a shared context failed.</param>
public sealed record DatabaseMigrationResult(IReadOnlyList<SharedMigrationResult> Shared, IReadOnlyList<TenantMigrationResult> Tenants)
{
    /// <summary>Gets a value indicating whether every shared context and every tenant is up to date.</summary>
    public bool Succeeded => Shared.All(s => s.Succeeded) && Tenants.All(t => t.Succeeded);
}

/// <summary>The outcome of migrating one shared context.</summary>
/// <param name="Schema">The context's schema.</param>
/// <param name="AppliedMigrations">The ids of the migrations this run applied, in order; empty when none was pending or it failed.</param>
/// <param name="Error">The failure, or <see langword="null"/> when the context is up to date.</param>
public sealed record SharedMigrationResult(string Schema, IReadOnlyList<string> AppliedMigrations, Exception? Error)
{
    /// <summary>Gets a value indicating whether the context is up to date.</summary>
    public bool Succeeded => Error is null;
}
