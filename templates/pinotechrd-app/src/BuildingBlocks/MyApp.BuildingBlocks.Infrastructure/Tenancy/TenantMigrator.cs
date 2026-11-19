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
/// <item>Once per migrator, under a global advisory lock: checks the server (PostgreSQL 16 or later) and the application
/// login (<see cref="TenantMigratorOptions.ApplicationRole"/>), installs the PostgreSQL extensions the tenant models
/// declare into the shared <see cref="TenantSchemaName.ExtensionsSchema"/> schema, reads the database's oid, which
/// scopes every role name to this database, and creates the database's group role (<see cref="TenantRoleName.SharedFor"/>)
/// with read rights on that schema and on the shared tables the tenant contexts map.</item>
/// <item>Takes the tenant's advisory lock on a connection of its own, creates the schema if it is missing, creates the
/// tenant's <c>NOLOGIN</c> role (<see cref="TenantRoleName.For"/>) and grants it data rights in that schema and the
/// application login membership in it, then, in a new scope set to that tenant in migration mode (its connections keep
/// the migrator's login), asks each module's context for its pending migrations and migrates it when there are any,
/// then runs each <see cref="ITenantSeeder"/> in registration order. Before each module and each seeder it checks that the
/// lock connection still holds the lock.</item>
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
/// <c>pg_advisory_lock</c> (see <see cref="AdvisoryLock"/>): a run that waits gives its connection back to the pool, so
/// runs queued on one tenant never use up the connections the run holding the lock needs.
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

    // A bigint advisory key shows in pg_locks as classid (high 32 bits), objid (low 32 bits) and objsubid 1.
    private const string LockHeldSql =
        "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_locks l, (SELECT pg_catalog.hashtextextended($1, 0) AS k) key "
        + "WHERE l.locktype = 'advisory' AND l.pid = pg_catalog.pg_backend_pid() AND l.granted AND l.objsubid = 1 "
        + "AND l.classid = ((key.k >> 32) & 4294967295)::oid AND l.objid = (key.k & 4294967295)::oid)";

    private const string SchemaExistsSql = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = $1)";

    private const string RoleExistsSql = "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = $1)";

    // Null when the role does not exist; true when it has an attribute a tenant or shared role must never have.
    private const string RolePrivilegedSql =
        "SELECT rolcanlogin OR rolsuper OR rolbypassrls OR rolcreaterole OR rolcreatedb OR rolreplication "
        + "FROM pg_catalog.pg_roles WHERE rolname = $1";

    // The first membership row of the role $1 (as role or as member) that the migrator does not establish itself, as
    // text, or null. Allowed rows, and nothing else:
    // - the creator's row: member current_user, WITH ADMIN, grantor the bootstrap superuser (oid 10), which PostgreSQL 16
    //   records when a login that is not a superuser creates a role;
    // - rows without ADMIN, granted by current_user (or by the bootstrap superuser when current_user is a superuser,
    //   which PostgreSQL records then):
    //   - tenant role ($2 is the shared role): the role a member of $2; the application login $3 (current_user when
    //     empty) a member of the role WITH INHERIT FALSE, SET TRUE;
    //   - shared role ($2 empty): a tenant role of this database (prefix $4) a member of the role.
    private const string RoleUnexpectedMembershipSql =
        "WITH r AS (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = $1), "
        + "cu AS (SELECT oid, rolsuper FROM pg_catalog.pg_roles WHERE rolname = CURRENT_USER) "
        + "SELECT pg_catalog.format('%I is a member of %I%s, granted by %I', m.rolname, g.rolname, "
        + "CASE WHEN a.admin_option THEN ' WITH ADMIN' ELSE '' END, gr.rolname) "
        + "FROM pg_catalog.pg_auth_members a JOIN r ON a.roleid = r.oid OR a.member = r.oid CROSS JOIN cu "
        + "JOIN pg_catalog.pg_roles m ON m.oid = a.member JOIN pg_catalog.pg_roles g ON g.oid = a.roleid "
        + "JOIN pg_catalog.pg_roles gr ON gr.oid = a.grantor "
        + "WHERE NOT ("
        + "(a.roleid = r.oid AND a.admin_option AND a.member = cu.oid AND a.grantor = 10::oid) "
        + "OR (NOT a.admin_option AND (a.grantor = cu.oid OR (a.grantor = 10::oid AND cu.rolsuper)) AND ("
        + "(a.member = r.oid AND $2 <> '' AND g.rolname = $2) "
        + "OR (a.roleid = r.oid AND $2 <> '' AND m.rolname = COALESCE(NULLIF($3, ''), CURRENT_USER) "
        + "AND NOT a.inherit_option AND a.set_option) "
        + "OR (a.roleid = r.oid AND $2 = '' AND pg_catalog.starts_with(m.rolname, $4) AND m.rolname ~ '^t[0-9]+_[0-9a-f]{32}$')))) "
        + "ORDER BY a.oid LIMIT 1";

    private const string RelationExistsSql =
        "SELECT EXISTS (SELECT 1 FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace "
        + "WHERE n.nspname = $1 AND c.relname = $2)";

    // Membership options (GRANT ... WITH INHERIT FALSE, SET TRUE) came with PostgreSQL 16.
    private const int MinimumServerVersion = 16;

    // How many times a tenant's grants are tried when a run on another database changes the same role rows.
    private const int GrantAttempts = 3;

    private const string ExtensionSchemaSql =
        "SELECT n.nspname, e.extrelocatable FROM pg_catalog.pg_extension e "
        + "JOIN pg_catalog.pg_namespace n ON n.oid = e.extnamespace WHERE e.extname = $1";

    private readonly IServiceScopeFactory _scopes;
    private readonly TenantDataSource _dataSource;
    private readonly List<IModuleMigrations> _modules;
    private readonly List<ITenantSeeder> _seeders;
    private readonly SemaphoreSlim _slots;
    private readonly string? _applicationRoleName;
    private readonly string? _applicationRole;
    private volatile bool _extensionsReady;

    // Read under the global lock before _extensionsReady is set, which publishes it.
    private uint _databaseOid;

    public TenantMigrator(
        IServiceScopeFactory scopes,
        TenantDataSource dataSource,
        IEnumerable<IModuleMigrations> modules,
        IEnumerable<ITenantSeeder> seeders,
        IOptions<TenantMigratorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentNullException.ThrowIfNull(modules);
        ArgumentNullException.ThrowIfNull(seeders);
        ArgumentNullException.ThrowIfNull(options);

        _applicationRoleName = options.Value.ApplicationRole;
        _applicationRole = _applicationRoleName is { } role ? QuoteRole(role) : null;
        _scopes = scopes;
        _dataSource = dataSource;
        _modules = Order(modules);
        // Registration order, as the container gives them.
        _seeders = [.. seeders];
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
                // The role first: a role that cannot be used stops the run before anything is created.
                var roleName = TenantRoleName.For(_databaseOid, tenantId);
                await EnsureRoleAsync(lockConnection, roleName, TenantRoleName.SharedFor(_databaseOid), string.Empty, cancellationToken)
                    .ConfigureAwait(false);
                schemaCreated = await EnsureSchemaAsync(lockConnection, schemaName, cancellationToken).ConfigureAwait(false);
                await GrantTenantRoleAsync(lockConnection, tenantId, roleName, cancellationToken).ConfigureAwait(false);
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
            // The migrations run as the migrator's login, not the tenant's role: the login owns what they create, and
            // the tenant's role gets its rights from GrantTenantRoleAsync.
            scope.ServiceProvider.GetRequiredService<CurrentTenant>().InitializeForMigration(tenantId);
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

            // The seeders last, in the same scope and under the same lock: every table they write exists by now, and two
            // runs on one tenant never seed at once. A seeder's failure is the tenant's failure.
            foreach (var seeder in _seeders)
            {
                await EnsureLockHeldAsync(lockConnection, TenantLockPrefix + TenantSchemaName.For(tenantId), cancellationToken)
                    .ConfigureAwait(false);
                await seeder.SeedAsync(scope.ServiceProvider, tenantId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Once per migrator, under the global lock: checks the server and the application login, installs the extensions every
    // tenant model declares into the shared extensions schema (a migration's own CREATE EXTENSION IF NOT EXISTS is then a
    // no-op, instead of installing the extension into the first tenant's schema), and gives the shared group role its
    // read rights. Those grants run here, once, and not per tenant: concurrent grants on one table collide on its ACL.
    private async Task EnsureExtensionsAsync(CancellationToken cancellationToken)
    {
        if (_extensionsReady)
        {
            return;
        }

        var names = DeclaredExtensions();
        var sharedTables = SharedTables();
        await using var connection = await LockAsync(ExtensionsLockKey, cancellationToken).ConfigureAwait(false);
        uint databaseOid;
        try
        {
            await EnsureServerAsync(connection, cancellationToken).ConfigureAwait(false);
            databaseOid = await ScalarAsync(connection, TenantRoleName.DatabaseOidSql, cancellationToken).ConfigureAwait(false) as uint?
                ?? throw new InvalidOperationException("The current database was not found in pg_database.");

            // Checked first: CREATE SCHEMA IF NOT EXISTS needs CREATE on the database even when the schema exists.
            _ = await EnsureSchemaAsync(connection, TenantSchemaName.ExtensionsSchema, cancellationToken).ConfigureAwait(false);
            foreach (var name in names)
            {
                await EnsureExtensionAsync(connection, name, cancellationToken).ConfigureAwait(false);
            }

            var sharedName = TenantRoleName.SharedFor(databaseOid);
            var shared = TenantRoleName.Quoted(sharedName);
            await EnsureRoleAsync(
                    connection, sharedName, sharedRoleName: null, sharedName[..^TenantRoleName.SharedSuffix.Length] + "_", cancellationToken)
                .ConfigureAwait(false);
            await ExecuteAsync(
                    connection, $"GRANT USAGE ON SCHEMA {TenantSchemaName.Quoted(TenantSchemaName.ExtensionsSchema)} TO {shared}", cancellationToken)
                .ConfigureAwait(false);

            // A shared table that does not exist yet is granted by a later run.
            foreach (var (schema, table) in sharedTables)
            {
                if (await ScalarAsync(connection, RelationExistsSql, cancellationToken, schema, table).ConfigureAwait(false) is true)
                {
                    await ExecuteAsync(
                            connection,
                            $"GRANT USAGE ON SCHEMA {TenantSchemaName.Quoted(schema)} TO {shared}; "
                            + $"GRANT SELECT ON {TenantSchemaName.Quoted(schema)}.{TenantSchemaName.Quoted(table)} TO {shared}",
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await UnlockAsync(connection, ExtensionsLockKey).ConfigureAwait(false);
        }

        _databaseOid = databaseOid;
        _extensionsReady = true;
    }

    // Membership options (WITH INHERIT FALSE, SET TRUE) need PostgreSQL 16; a named application login must exist.
    private async Task EnsureServerAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        if (connection.PostgreSqlVersion.Major < MinimumServerVersion)
        {
            throw new InvalidOperationException(
                $"Tenant database roles need PostgreSQL {MinimumServerVersion} or later (membership WITH INHERIT FALSE, SET TRUE); "
                + $"this server is {connection.PostgreSqlVersion}.");
        }

        if (_applicationRoleName is not null
            && await ScalarAsync(connection, RoleExistsSql, cancellationToken, _applicationRoleName).ConfigureAwait(false) is not true)
        {
            throw new InvalidOperationException(
                $"The application login {_applicationRole} named by Migrator:ApplicationRole (TenantMigratorOptions.ApplicationRole) "
                + "does not exist; create it, or leave the setting empty when the application connects with the migrator's own login.");
        }
    }

    // Every run, under the tenant's lock, so a role that is missing, a membership, or a table added since the last run is
    // put right. The tenant's role gets data rights in its own schema only, never CREATE: the migrator's login owns the
    // schema and its tables, and default privileges give the role the tables later migrations create.
    private async Task GrantTenantRoleAsync(NpgsqlConnection connection, Guid tenantId, string roleName, CancellationToken cancellationToken)
    {
        // DDL takes no parameters: every name passed its pattern and is quoted. One batch, so one implicit transaction:
        // a run on another database that changes the same memberships at the same moment fails it whole, and it is retried.
        var role = TenantRoleName.Quoted(roleName);
        var schema = TenantSchemaName.Quoted(TenantSchemaName.For(tenantId));
        var sql = $"GRANT {TenantRoleName.Quoted(TenantRoleName.SharedFor(_databaseOid))} TO {role}; "
            + $"GRANT {role} TO {_applicationRole ?? "CURRENT_USER"} WITH INHERIT FALSE, SET TRUE; "
            + $"GRANT USAGE ON SCHEMA {schema} TO {role}; "
            + $"ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA {schema} GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO {role}; "
            + $"ALTER DEFAULT PRIVILEGES FOR ROLE CURRENT_USER IN SCHEMA {schema} GRANT USAGE, SELECT ON SEQUENCES TO {role}; "
            + $"GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {schema} TO {role}; "
            + $"GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {schema} TO {role}";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await ExecuteAsync(connection, sql, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PostgresException exception) when (attempt < GrantAttempts && IsConcurrentRoleChange(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * attempt), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    // Role names are scoped to this database by its oid, but roles belong to the cluster and advisory locks to one
    // database: a migrator connected to the same database through another route may create the same role at the same
    // moment, which is fine. A role that already exists, created earlier by this migrator or by anyone else, is checked on
    // every run and adopted only when it has no power of its own (a login, a superuser...) and no membership the migrator
    // does not establish itself (see RoleUnexpectedMembershipSql): the application's login would gain that power by
    // setting it, and a login holding it, or ADMIN on it, would reach the tenant's data. The same holds for a role a
    // concurrent run created. The shared role passes a null sharedRoleName.
    private async Task EnsureRoleAsync(
        NpgsqlConnection connection, string roleName, string? sharedRoleName, string tenantRolePrefix, CancellationToken cancellationToken)
    {
        if (await ScalarAsync(connection, RolePrivilegedSql, cancellationToken, roleName).ConfigureAwait(false) is null)
        {
            try
            {
                await ExecuteAsync(connection, $"CREATE ROLE {TenantRoleName.Quoted(roleName)} NOLOGIN", cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PostgresException exception) when (exception.SqlState != PostgresErrorCodes.InternalError && IsConcurrentRoleChange(exception))
            {
                // Created meanwhile by another run: checked below like any existing role.
            }
        }

        await EnsureRoleAdoptableAsync(connection, roleName, sharedRoleName, tenantRolePrefix, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureRoleAdoptableAsync(
        NpgsqlConnection connection, string roleName, string? sharedRoleName, string tenantRolePrefix, CancellationToken cancellationToken)
    {
        var quoted = TenantRoleName.Quoted(roleName);
        switch (await ScalarAsync(connection, RolePrivilegedSql, cancellationToken, roleName).ConfigureAwait(false))
        {
            case null:
                throw new InvalidOperationException($"The role {quoted} was dropped while the migrator created it; run the migrator again.");
            case true:
                throw new InvalidOperationException(
                    $"The role {quoted} already exists with LOGIN, SUPERUSER, BYPASSRLS, CREATEROLE, "
                    + "CREATEDB or REPLICATION. A tenant role must have none of them, so it is not adopted: remove them (ALTER "
                    + "ROLE ... NOLOGIN NOSUPERUSER NOBYPASSRLS NOCREATEROLE NOCREATEDB NOREPLICATION) or drop the role, then "
                    + "run the migrator again.");
        }

        var unexpected = await ScalarAsync(
                connection,
                RoleUnexpectedMembershipSql,
                cancellationToken,
                roleName,
                sharedRoleName ?? string.Empty,
                _applicationRoleName ?? string.Empty,
                tenantRolePrefix)
            .ConfigureAwait(false);
        if (unexpected is string membership)
        {
            throw new InvalidOperationException(
                $"The role {quoted} has a membership the migrator does not establish ({membership}), so it is not adopted: a "
                + "member, or a login holding ADMIN on it, could reach the tenant's data. Revoke that membership (REVOKE ... "
                + "GRANTED BY ...) or drop the role, then run the migrator again (README, Existing roles).");
        }
    }

    // What a concurrent run on another database of the cluster can cause on the shared role catalogs: the role created
    // meanwhile (42710, or 23505 on pg_authid's unique index), a membership inserted meanwhile (23505 on
    // pg_auth_members), or a row updated meanwhile (XX000, "tuple concurrently updated").
    private static bool IsConcurrentRoleChange(PostgresException exception) =>
        exception.SqlState is PostgresErrorCodes.DuplicateObject or PostgresErrorCodes.InternalError
        || (exception.SqlState == PostgresErrorCodes.UniqueViolation
            && exception.ConstraintName is { } constraint
            && constraint.StartsWith("pg_auth", StringComparison.Ordinal));

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<object?> ScalarAsync(
        NpgsqlConnection connection, string sql, CancellationToken cancellationToken, params string[] values)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    // The tables the tenant contexts read but never migrate (ExcludeFromMigrations), such as the catalog's: the shared
    // group role may read them. Each name passes the identifier pattern before it reaches SQL.
    private List<(string Schema, string Table)> SharedTables()
    {
        using var scope = _scopes.CreateScope();
        return _modules
            .SelectMany(m => ModelOf(m.GetContext(scope.ServiceProvider)).GetRelationalModel().Tables)
            .Where(t => t.IsExcludedFromMigrations && t.Schema is not null)
            .Select(t => (TenantSchemaName.Validate(t.Schema), TenantSchemaName.Validate(t.Name)))
            .Distinct()
            .Order()
            .ToList();
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
    private Task<NpgsqlConnection> LockAsync(string key, CancellationToken cancellationToken) =>
        AdvisoryLock.AcquireAsync(_dataSource.Value, key, cancellationToken);

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

    // Not cancellable: the lock is released even when the run was cancelled (see AdvisoryLock.ReleaseAsync).
    private static Task UnlockAsync(NpgsqlConnection connection, string key) => AdvisoryLock.ReleaseAsync(connection, key);

    private static IModel ModelOf(DbContext context) => context.GetService<IDesignTimeModel>().Model;

    // The schema of a tenant context comes from search_path only. A schema anywhere in the model (default schema,
    // tables, split tables, views, functions, procedures, sequences) would send every tenant to that one schema. An
    // extension may name only the shared extensions schema. Raw SQL in a migration (migrationBuilder.Sql) cannot be checked.
    // A table excluded from migrations (ExcludeFromMigrations) is skipped: the context reads it but never creates it, as
    // the Access context does with the shared catalog tables, which the catalog's own context migrates (ADR 0010). No
    // migrated table may hold a foreign key to such a table, or to any table that names a schema.
    private static void EnsureNoSchemaInModel(DbContext context, string moduleName)
    {
        var model = ModelOf(context);
        var relational = model.GetRelationalModel();
        var schemas = relational.Tables.Where(t => !t.IsExcludedFromMigrations).Select(t => t.Schema)
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

        // A foreign key from a migrated table to an excluded table, or to a table in a schema, would tie every tenant's
        // rows to another owner's table (the catalog, or another tenant's schema).
        var foreignKey = relational.Tables.Where(t => !t.IsExcludedFromMigrations)
            .SelectMany(t => t.ForeignKeyConstraints)
            .FirstOrDefault(k => k.PrincipalTable.Schema is not null || k.PrincipalTable.IsExcludedFromMigrations);
        if (foreignKey is not null)
        {
            throw new InvalidOperationException(
                $"The context of the module '{moduleName}' ({context.GetType().FullName}) has the foreign key '{foreignKey.Name}' "
                + $"from the tenant table '{foreignKey.Table.Name}' to '{foreignKey.PrincipalTable.Name}', a table outside the "
                + "tenant's migrations. A tenant table references only tables of its own schema; match others by value.");
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
