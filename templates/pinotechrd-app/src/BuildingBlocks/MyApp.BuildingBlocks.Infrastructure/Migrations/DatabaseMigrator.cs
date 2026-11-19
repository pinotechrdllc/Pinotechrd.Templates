using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// The <see cref="IDatabaseMigrator"/>. A run:
/// <list type="number">
/// <item>Takes the global advisory lock <see cref="GlobalLockKey"/> on a connection of its own (the same try-and-wait
/// loop as the tenant locks), so two migrator runs take turns and each migration is applied once.</item>
/// <item>Migrates each shared context in registration order, after checking that its model keeps every object in its
/// schema; a failure stops the run.</item>
/// <item>Reads every tenant id from the <see cref="ITenantDirectory"/> before any tenant starts, so the directory's
/// connection is back in the pool before the tenants use it, then migrates them through the
/// <see cref="ITenantMigrator"/>, each under its own tenant lock.</item>
/// </list>
/// The lock connection is the one the tenant migrator leaves free in the pool, so the pool needs at least
/// <see cref="MinimumPoolSize"/> connections.
/// </summary>
public sealed class DatabaseMigrator : IDatabaseMigrator
{
    /// <summary>The key of the global migration lock, taken as <c>pg_advisory_lock(hashtextextended(key, 0))</c>.</summary>
    public const string GlobalLockKey = "database-migration";

    /// <summary>The smallest pool a run fits in: the global lock connection and one tenant's two connections.</summary>
    public const int MinimumPoolSize = 3;

    private readonly IServiceScopeFactory _scopes;
    private readonly NpgsqlDataSource _dataSource;
    private readonly List<ISharedMigrations> _shared;
    private readonly ITenantMigrator _tenants;

    internal DatabaseMigrator(IServiceScopeFactory scopes, TenantDataSource dataSource, IEnumerable<ISharedMigrations> shared, ITenantMigrator tenants)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(shared);
        ArgumentNullException.ThrowIfNull(tenants);

        var settings = new NpgsqlConnectionStringBuilder(dataSource.Value.ConnectionString);
        if (settings.Pooling && settings.MaxPoolSize < MinimumPoolSize)
        {
            throw new InvalidOperationException(
                $"The database migrator needs a 'Maximum Pool Size' of at least {MinimumPoolSize}: it holds the global lock "
                + "connection while a tenant's run holds its lock connection and its migration connection.");
        }

        _scopes = scopes;
        _dataSource = dataSource.Value;
        _shared = [.. shared];
        _tenants = tenants;
    }

    /// <inheritdoc />
    public async Task<DatabaseMigrationResult> MigrateAsync(int maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);

        await using var lockConnection = await AdvisoryLock.AcquireAsync(_dataSource, GlobalLockKey, cancellationToken).ConfigureAwait(false);
        try
        {
            var shared = new List<SharedMigrationResult>(_shared.Count);
            foreach (var context in _shared)
            {
                var result = await MigrateSharedAsync(context, cancellationToken).ConfigureAwait(false);
                shared.Add(result);
                if (!result.Succeeded)
                {
                    return new DatabaseMigrationResult(shared, []);
                }
            }

            var tenantIds = (await ListTenantsAsync(cancellationToken).ConfigureAwait(false)).Distinct().ToList();
            var tenants = await MigrateTenantsAsync(tenantIds, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
            return new DatabaseMigrationResult(shared, tenants);
        }
        finally
        {
            await AdvisoryLock.ReleaseAsync(lockConnection, GlobalLockKey).ConfigureAwait(false);
        }
    }

    private async Task<SharedMigrationResult> MigrateSharedAsync(ISharedMigrations shared, CancellationToken cancellationToken)
    {
        try
        {
            var scope = _scopes.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var context = shared.GetContext(scope.ServiceProvider);
                EnsureModelInSchema(context, shared.Schema);
                var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                if (pending.Count > 0)
                {
                    await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                }

                return new SharedMigrationResult(shared.Schema, pending, Error: null);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new SharedMigrationResult(shared.Schema, [], exception);
        }
    }

    // A tenant id with no schema (the empty GUID) fails on its own: the tenant migrator would refuse the whole list for it.
    // Every other tenant still runs, and the results keep the directory's order.
    private async Task<List<TenantMigrationResult>> MigrateTenantsAsync(
        List<Guid> tenantIds, int maxDegreeOfParallelism, CancellationToken cancellationToken)
    {
        var valid = tenantIds.Where(id => id != Guid.Empty).ToList();
        var migrated = valid.Count == 0
            ? []
            : await _tenants.MigrateAsync(valid, maxDegreeOfParallelism, cancellationToken).ConfigureAwait(false);
        var byId = migrated.ToDictionary(r => r.TenantId);
        return [.. tenantIds.Select(id => id == Guid.Empty
            ? new TenantMigrationResult(id, SchemaName: string.Empty, SchemaCreated: false, [], InvalidTenantId())
            : byId[id])];
    }

    private static ArgumentException InvalidTenantId() =>
        new("The tenant id is the empty GUID, which names no tenant schema: fix the row in the tenant directory.", "tenantId");

    private async Task<IReadOnlyList<Guid>> ListTenantsAsync(CancellationToken cancellationToken)
    {
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            var directory = scope.ServiceProvider.GetService<ITenantDirectory>();
            return directory is null ? [] : await directory.ListTenantIdsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // A shared context keeps every object it migrates in its one schema. An object with no schema would land wherever
    // the connection's search_path points, and an object in another schema would be another owner's. PostgreSQL's own
    // objects count too: an extension, enum, range or collation declared without that schema is refused.
    private static void EnsureModelInSchema(DbContext context, string schema)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var relational = model.GetRelationalModel();
        var outside = relational.Tables.Where(t => !t.IsExcludedFromMigrations).Select(t => (t.Schema, t.Name))
            .Concat(relational.Views.Select(v => (v.Schema, v.Name)))
            .Concat(relational.Functions.Select(f => (f.Schema, f.Name)))
            .Concat(relational.StoredProcedures.Select(p => (p.Schema, p.Name)))
            .Concat(relational.Sequences.Select(s => (s.Schema, s.Name)))
            .Concat(model.GetPostgresExtensions().Select(e => (e.Schema, e.Name)))
            .Concat(model.GetPostgresEnums().Select(e => (e.Schema, e.Name)))
            .Concat(model.PostgresRanges().Select(r => (r.Schema, r.Name)))
            .Concat(model.GetCollations().Select(c => (c.Schema, c.Name)))
            .FirstOrDefault(o => !string.Equals(o.Schema, schema, StringComparison.Ordinal));
        if (outside.Name is not null)
        {
            throw new InvalidOperationException(
                $"The shared context {context.GetType().FullName} maps '{outside.Name}' outside its schema '{schema}' "
                + $"(schema: {outside.Schema ?? "none"}). A shared context keeps every object in its schema: set it with HasDefaultSchema.");
        }
    }
}
