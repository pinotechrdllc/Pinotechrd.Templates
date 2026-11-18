using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// The <see cref="ITenantMigrator"/>. A tenant's run:
/// <list type="number">
/// <item>Waits for one of the migrator's slots (see <see cref="Capacity"/>), so concurrent runs never need more
/// connections than the pool holds.</item>
/// <item>Once per migrator, installs the PostgreSQL extensions the tenant models declare into the shared
/// <see cref="TenantSchemaName.ExtensionsSchema"/> schema, under a global advisory lock.</item>
/// <item>Takes the tenant's advisory lock on a connection of its own, creates the schema if it is missing, then, in a new
/// scope set to that tenant, asks each module's context for its pending migrations and migrates it when there are any.
/// Before each module it checks that the lock connection still holds the lock.</item>
/// </list>
/// <para>
/// Why a session lock on its own connection, and not <c>pg_advisory_xact_lock</c>: EF Core migrates in transactions of
/// its own, one per migration, and refuses to migrate inside a transaction the caller started, so a transaction lock
/// cannot be taken on the migration's connection; EF also closes that connection between steps, and the pool's reset
/// would drop a session lock held there. A separate connection keeps the lock for the whole run, schema creation
/// included (two concurrent <c>CREATE SCHEMA IF NOT EXISTS</c> can fail with a unique violation), and leaves no
/// transaction idle. The lock session turns <c>idle_session_timeout</c> off for itself (PostgreSQL 14 or later), so a
/// server limit cannot end it while it waits for the migrations; the pool's reset restores the setting.
/// </para>
/// <para>
/// The lock is taken with <c>pg_try_advisory_lock</c>, retried with a growing pause, not with the blocking
/// <c>pg_advisory_lock</c>: a run that waits gives its connection back to the pool, so runs queued on one tenant never
/// use up the connections the run holding the lock needs.
/// </para>
/// </summary>
internal sealed partial class TenantMigrator : ITenantMigrator, IDisposable
{
    /// <summary>The module migrated first in every tenant, when it is registered.</summary>
    internal const string FirstModule = "Access";

    /// <summary>Connections one tenant's run holds at most: its lock connection and its migration connection.</summary>
    internal const int ConnectionsPerTenant = 2;

    // Lock keys are 64-bit hashes of these texts: a collision only makes two runs take turns.
    private const string TenantLockPrefix = "tenant-migration:";
    private const string ExtensionsLockKey = "tenant-migration-extensions";

    private const string TryLockSql =
        "SELECT pg_catalog.set_config('idle_session_timeout', '0', false), pg_catalog.pg_try_advisory_lock(pg_catalog.hashtextextended($1, 0))";

    private const string UnlockSql = "SELECT pg_catalog.pg_advisory_unlock(pg_catalog.hashtextextended($1, 0))";

    // A bigint advisory key shows in pg_locks as classid (high 32 bits), objid (low 32 bits) and objsubid 1.
    private const string LockHeldSql =
        "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks l, (SELECT pg_catalog.hashtextextended($1, 0) AS k) key "
        + "WHERE l.locktype = 'advisory' AND l.pid = pg_catalog.pg_backend_pid() AND l.granted AND l.objsubid = 1 "
        + "AND l.classid = ((key.k >> 32) & 4294967295)::oid AND l.objid = (key.k & 4294967295)::oid)";

    private const string SchemaExistsSql = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = $1)";

    private const string ExtensionSchemaSql =
        "SELECT n.nspname, e.extrelocatable FROM pg_catalog.pg_extension e "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = $1";

    private static readonly TimeSpan FirstPause = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly TenantDataSource _dataSource;
    private readonly List<IModuleMigrations> _modules;
    private readonly SemaphoreSlim _slots;
    private readonly string? _applicationRole;
    private volatile bool _extensionsReady;

    public TenantMigrator(
        IServiceScopeFactory scopes, TenantDataSource dataSource, IEnumerable<IModuleMigrations> modules, IOptions<TenantMigratorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(options);

        _applicationRole = options.Value.ApplicationRole is { } role ? QuoteRole(role) : null;
        _scopes = scopes;
        _dataSource = dataSource;
        _modules = Order(modules);
        Capacity = CapacityOf(dataSource.Value);
        _slots = new SemaphoreSlim(Capacity, Capacity);
    }

    /// <summary>
    /// How many tenants this migrator runs at once, whatever is asked: each run holds up to
    /// <see cref="ConnectionsPerTenant"/> connections, and one connection of the pool is left for anything else, so it is
    /// <c>(Maximum Pool Size - 1) / 2</c>, at least 1 (a pool of 2 or 3 runs one tenant at a time). Unbounded without
    /// pooling.
    /// </summary>
    internal int Capacity { get; }

    public Task<TenantMigrationResult> MigrateAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var schemaName = TenantSchemaName.For(tenantId);
        return RunAsync(tenantId, schemaName, cancellationToken);
    }

    public async Task<IReadOnlyList<TenantMigrationResult>> MigrateAsync(
        IEnumerable<Guid> tenantIds, int maxDegreeOfParallelism, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(tenantIds);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxDegreeOfParallelism, 1);

        var tenants = tenantIds.Distinct().ToArray();
        // Rejects an empty id before any tenant runs.
        var schemas = tenants.Select(TenantSchemaName.For).ToArray();

        var results = new TenantMigrationResult[tenants.Length];
        var parallel = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(maxDegreeOfParallelism, Capacity),
            CancellationToken = cancellationToken,
        };
        await Parallel.ForEachAsync(
                Enumerable.Range(0, tenants.Length),
                parallel,
                async (index, token) => results[index] = await RunAsync(tenants[index], schemas[index], token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return results;
    }

    public void Dispose() => _slots.Dispose();

    private async Task<TenantMigrationResult> RunAsync(Guid tenantId, string schemaName, CancellationToken cancellationToken)
    {
        var modules = new List<ModuleMigrationResult>(_modules.Count);
        var schemaCreated = false;
        await _slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureExtensionsAsync(cancellationToken).ConfigureAwait(false);

            var lockKey = TenantLockPrefix + schemaName;
            await using var lockConnection = await LockAsync(lockKey, cancellationToken).ConfigureAwait(false);
            try
            {
                schemaCreated = await EnsureSchemaAsync(lockConnection, schemaName, cancellationToken).ConfigureAwait(false);
                await MigrateModulesAsync(tenantId, lockConnection, modules, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                await UnlockAsync(lockConnection, lockKey).ConfigureAwait(false);
            }

            return new TenantMigrationResult(tenantId, schemaName, schemaCreated, modules, Error: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // Any failure of one tenant is reported in its result, so the other tenants go on.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return new TenantMigrationResult(tenantId, schemaName, schemaCreated, modules, exception);
        }
        finally
        {
            _slots.Release();
        }
    }

    private async Task MigrateModulesAsync(
        Guid tenantId, NpgsqlConnection lockConnection, List<ModuleMigrationResult> results, CancellationToken cancellationToken)
    {
        var scope = _scopes.CreateAsyncScope();
        await using (scope.ConfigureAwait(false))
        {
            scope.ServiceProvider.GetRequiredService<ICurrentTenantInitializer>().Initialize(tenantId);
            foreach (var module in _modules)
            {
                try
                {
                    await EnsureLockHeldAsync(lockConnection, TenantLockPrefix + TenantSchemaName.For(tenantId), cancellationToken)
                        .ConfigureAwait(false);
                    var context = module.GetContext(scope.ServiceProvider);
                    EnsureNoSchemaInModel(context, module.ModuleName);
                    var pending = (await context.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).ToList();
                    if (pending.Count > 0)
                    {
                        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
                    }

                    results.Add(new ModuleMigrationResult(module.ModuleName, pending, Succeeded: true));
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                {
                    // What a failed module applied before the failure is in its history table, not here.
                    results.Add(new ModuleMigrationResult(module.ModuleName, [], Succeeded: false));
                    throw;
                }
            }
        }
    }

    // The extensions every tenant model declares, installed once into the shared extensions schema. A migration's own
    // CREATE EXTENSION IF NOT EXISTS is then a no-op, instead of installing the extension into the first tenant's schema.
    private async Task EnsureExtensionsAsync(CancellationToken cancellationToken)
    {
        if (_extensionsReady)
        {
            return;
        }

        var names = DeclaredExtensions();
        if (names.Count > 0 || _applicationRole is not null)
        {
            await using var connection = await LockAsync(ExtensionsLockKey, cancellationToken).ConfigureAwait(false);
            try
            {
                // Checked first: CREATE SCHEMA IF NOT EXISTS needs CREATE on the database even when the schema exists.
                _ = await EnsureSchemaAsync(connection, TenantSchemaName.ExtensionsSchema, cancellationToken).ConfigureAwait(false);
                foreach (var name in names)
                {
                    await EnsureExtensionAsync(connection, name, cancellationToken).ConfigureAwait(false);
                }

                if (_applicationRole is not null)
                {
                    await using var grant = new NpgsqlCommand(
                        $"GRANT USAGE ON SCHEMA {TenantSchemaName.Quoted(TenantSchemaName.ExtensionsSchema)} TO {_applicationRole}",
                        connection);
                    await grant.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                await UnlockAsync(connection, ExtensionsLockKey).ConfigureAwait(false);
            }
        }

        _extensionsReady = true;
    }

    private static async Task EnsureExtensionAsync(NpgsqlConnection connection, string name, CancellationToken cancellationToken)
    {
        await using (var find = new NpgsqlCommand(ExtensionSchemaSql, connection) { Parameters = { new NpgsqlParameter { Value = name } } })
        await using (var reader = await find.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var schema = reader.GetString(0);
                if (string.Equals(schema, TenantSchemaName.ExtensionsSchema, StringComparison.Ordinal))
                {
                    return;
                }

                var advice = reader.GetBoolean(1)
                    ? $"Move it with ALTER EXTENSION \"{name}\" SET SCHEMA {TenantSchemaName.ExtensionsSchema}."
                    : $"It cannot be moved (it is not relocatable): drop it and create it again with CREATE EXTENSION \"{name}\" "
                      + $"SCHEMA {TenantSchemaName.ExtensionsSchema}, after checking what depends on it (DROP EXTENSION drops "
                      + "dependent objects only with CASCADE).";
                throw new InvalidOperationException(
                    $"The extension '{name}' is installed in the schema '{schema}', not '{TenantSchemaName.ExtensionsSchema}': "
                    + $"tenant connections see only their own schema and '{TenantSchemaName.ExtensionsSchema}'. {advice}");
            }
        }

        // DDL takes no parameters: the name matched the extension pattern and is quoted. CASCADE installs the
        // extensions it requires (earthdistance needs cube) in the same schema.
        await using var install = new NpgsqlCommand(
            $"CREATE EXTENSION IF NOT EXISTS \"{name}\" SCHEMA {TenantSchemaName.Quoted(TenantSchemaName.ExtensionsSchema)} CASCADE", connection);
        await install.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private List<string> DeclaredExtensions()
    {
        using var scope = _scopes.CreateScope();
        return _modules
            .SelectMany(m => ModelOf(m.GetContext(scope.ServiceProvider)).GetPostgresExtensions())
            .Select(e => ExtensionNamePattern().IsMatch(e.Name)
                ? e.Name
                : throw new InvalidOperationException($"The extension name '{e.Name}' is not valid: it must match {ExtensionNamePattern()}."))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
    }

    // Returns an open connection that holds the lock. While another run holds it, waits without a connection.
    private async Task<NpgsqlConnection> LockAsync(string key, CancellationToken cancellationToken)
    {
        var pause = FirstPause;
        while (true)
        {
            var connection = await _dataSource.Value.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await using var command = new NpgsqlCommand(TryLockSql, connection) { Parameters = { new NpgsqlParameter { Value = key } } };
                await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false) && reader.GetBoolean(1))
                {
                    return connection;
                }
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            await connection.DisposeAsync().ConfigureAwait(false);
            await Task.Delay(pause, cancellationToken).ConfigureAwait(false);
            pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestPause.Ticks));
        }
    }

    // A session that ended took its lock with it; the run must not go on without it.
    private static async Task EnsureLockHeldAsync(NpgsqlConnection lockConnection, string key, CancellationToken cancellationToken)
    {
        bool held;
        try
        {
            await using var command = new NpgsqlCommand(LockHeldSql, lockConnection) { Parameters = { new NpgsqlParameter { Value = key } } };
            held = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
        }
        catch (NpgsqlException exception)
        {
            throw new InvalidOperationException("The tenant's migration lock was lost with its connection; the run stops.", exception);
        }

        if (!held)
        {
            throw new InvalidOperationException("The tenant's migration lock is no longer held; the run stops.");
        }
    }

    // Under the lock, so the answer holds until the schema is created.
    private static async Task<bool> EnsureSchemaAsync(NpgsqlConnection connection, string schemaName, CancellationToken cancellationToken)
    {
        await using (var exists = new NpgsqlCommand(SchemaExistsSql, connection) { Parameters = { new NpgsqlParameter { Value = schemaName } } })
        {
            if (await exists.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true)
            {
                return false;
            }
        }

        // DDL takes no parameters: the name passed the pattern and is quoted.
        await using var create = new NpgsqlCommand($"CREATE SCHEMA IF NOT EXISTS {TenantSchemaName.Quoted(schemaName)}", connection);
        await create.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Not cancellable: the lock is released even when the run was cancelled. A broken connection already lost the lock
    // with its session, so that failure must not hide the run's own outcome.
    private static async Task UnlockAsync(NpgsqlConnection connection, string key)
    {
        try
        {
            await using var command = new NpgsqlCommand(UnlockSql, connection) { Parameters = { new NpgsqlParameter { Value = key } } };
            await command.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (NpgsqlException)
        {
            // The session, and its lock, ended with the connection.
        }
        catch (InvalidOperationException)
        {
            // The connection is no longer open: same as above.
        }
    }

    private static IModel ModelOf(DbContext context) => context.GetService<IDesignTimeModel>().Model;

    // The schema of a tenant context comes from search_path only. A schema anywhere in the model (default schema,
    // tables, split tables, views, functions, procedures, sequences) would send every tenant to that one schema. An
    // extension may name only the shared extensions schema. Raw SQL in a migration (migrationBuilder.Sql) cannot be checked.
    private static void EnsureNoSchemaInModel(DbContext context, string moduleName)
    {
        var model = ModelOf(context);
        var relational = model.GetRelationalModel();
        var schemas = relational.Tables.Select(t => t.Schema)
            .Concat(relational.Views.Select(v => v.Schema))
            .Concat(relational.Functions.Select(f => f.Schema))
            .Concat(relational.StoredProcedures.Select(p => p.Schema))
            .Concat(relational.Sequences.Select(s => s.Schema))
            .Append(model.GetDefaultSchema())
            .Where(s => s is not null)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (schemas.Count > 0)
        {
            throw new InvalidOperationException(
                $"The context of the module '{moduleName}' ({context.GetType().FullName}) names a schema in its model. A tenant "
                + "context names none: the tenant's schema is selected per connection by search_path.");
        }

        var misplaced = model.GetPostgresExtensions()
            .FirstOrDefault(e => e.Schema is not null && !string.Equals(e.Schema, TenantSchemaName.ExtensionsSchema, StringComparison.Ordinal));
        if (misplaced is not null)
        {
            throw new InvalidOperationException(
                $"The context of the module '{moduleName}' installs the extension '{misplaced.Name}' in a schema of its own. "
                + $"Declare extensions without a schema: the migrator installs them in the shared '{TenantSchemaName.ExtensionsSchema}' "
                + "schema, which every tenant connection sees after its own schema.");
        }
    }

    // Any role name PostgreSQL accepts (App-Role, a name with quotes), as a quoted identifier: every double quote is
    // doubled, so the name can never end the identifier. Refused: empty, a NUL, more than 63 bytes (PostgreSQL would cut it).
    private static string QuoteRole(string role)
    {
        if (string.IsNullOrEmpty(role) || role.Contains('\0', StringComparison.Ordinal) || System.Text.Encoding.UTF8.GetByteCount(role) > 63)
        {
            throw new ArgumentException(
                "TenantMigratorOptions.ApplicationRole must be a role name of 1 to 63 bytes without a NUL character.", nameof(role));
        }

        return "\"" + role.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static int CapacityOf(NpgsqlDataSource dataSource)
    {
        var settings = new NpgsqlConnectionStringBuilder(dataSource.ConnectionString);
        if (!settings.Pooling)
        {
            return int.MaxValue;
        }

        if (settings.MaxPoolSize < ConnectionsPerTenant)
        {
            throw new InvalidOperationException(
                $"The migrator needs a 'Maximum Pool Size' of at least {ConnectionsPerTenant}: a tenant's run holds its lock "
                + "connection and its migration connection at once.");
        }

        return Math.Max(1, (settings.MaxPoolSize - 1) / ConnectionsPerTenant);
    }

    private static List<IModuleMigrations> Order(IEnumerable<IModuleMigrations> modules)
    {
        var ordered = modules
            .OrderBy(m => string.Equals(m.ModuleName, FirstModule, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(m => m.ModuleName, StringComparer.Ordinal)
            .ToList();

        // By history table: two spellings of one name (Sales, SALES) would share one history.
        var duplicate = ordered.GroupBy(m => TenantPersistenceRegistration.HistoryTableOf(m.ModuleName), StringComparer.Ordinal)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"The modules {string.Join(", ", duplicate.Select(m => $"'{m.ModuleName}'"))} share the history table "
                + $"'{duplicate.Key}': each module registers its migrations once, under one name.");
        }

        return ordered;
    }

    [GeneratedRegex(@"^[a-z0-9_-]{1,63}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ExtensionNamePattern();
}
