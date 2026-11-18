using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Transactions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Database;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Binds every connection of a tenant context to the current tenant, and refuses any command on a connection that is
/// not bound or that would change the binding. One instance per context options (per scope for a context registered with
/// <see cref="TenantPersistenceRegistration.AddTenantDbContext{TContext}"/>), holding that scope's
/// <see cref="ICurrentTenant"/>; no static or ambient tenant state.
/// <list type="bullet">
/// <item>Before EF opens a connection: a current tenant with a valid schema name is required, and no ambient
/// <see cref="Transaction"/> (an enlisted connection would run the bind inside that transaction, and its rollback would
/// undo it). Otherwise the open fails before anything reaches the server. At the first open it also checks that the
/// context's options still carry what the building block guarantees (this guard, the module's unqualified history
/// table, a connection string with no refused setting).</item>
/// <item>Right after the open, one statement reads the session's default <c>search_path</c>, whether a schema is named
/// like the database user, and whether the role may use the <c>extensions</c> schema; then sets <c>search_path</c> to the
/// tenant's schema and <c>extensions</c> (the value is a parameter) and reads back the value and <c>current_schema()</c>.
/// Unless the default is PostgreSQL's own (<c>"$user", public</c>, with no schema named like the user), the role may use
/// <c>extensions</c> when it exists, and the result is exactly the tenant's, and on any failure or cancellation, the
/// connection is closed before the exception goes on.</item>
/// <item>Every command EF runs on the context's connection must find it bound to the context's current tenant, and must
/// not change the session's path or role (<see cref="SessionStatements"/>). The binding is kept per connection object and
/// cleared whenever the connection changes state, so it never survives the pool's reuse of the physical connection. A
/// second context of the same tenant may adopt the connection (<c>SetDbConnection</c>) to share a transaction; a context
/// of another tenant is refused.</item>
/// <item>Opening the context's connection outside EF (<c>Database.GetDbConnection().Open()</c>) throws: use
/// <see cref="TenantDbContextOptionsExtensions.OpenTenantConnectionAsync"/> for raw SQL.</item>
/// </list>
/// The statement that binds a connection is not traced (it is recognized by its command type). The statement check is a
/// guard against mistakes in application SQL, not a security boundary (see <see cref="SessionStatements"/>).
/// </summary>
internal sealed class TenantConnectionGuard(ICurrentTenant currentTenant, string historyTable) : IDbConnectionInterceptor, IDbCommandInterceptor
{
    internal const string NoTenantMessage =
        "No current tenant: a tenant context cannot open a connection (there is no fallback to the public schema). "
        + "Set the tenant of the scope with ICurrentTenantInitializer first.";

    internal const string UnboundMessage =
        "The connection of this tenant context is not bound to the current tenant, so the command is refused. Let EF open "
        + "the connection, or use Database.OpenTenantConnectionAsync() for raw SQL; never open Database.GetDbConnection() "
        + "yourself.";

    internal const string ForeignOpenMessage =
        "The connection of a tenant context was opened outside EF Core, so it is not bound to the tenant. Use "
        + "Database.OpenTenantConnectionAsync() to get an open connection for raw SQL.";

    internal const string AmbientTransactionMessage =
        "A tenant context cannot open its connection inside an ambient transaction (TransactionScope): the connection would "
        + "enlist, the tenant's schema would be set inside that transaction, and a rollback would undo it. Open the "
        + "connection first, or use the context's own transactions (Database.BeginTransactionAsync).";

    /// <summary>PostgreSQL's own default <c>search_path</c>, which every new session must have.</summary>
    internal const string DefaultSearchPath = "\"$user\", public";

    private const string BindSql =
        "SELECT pg_catalog.current_setting('search_path'), "
        + "EXISTS (SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname IN (current_user, session_user)), "
        + "(SELECT pg_catalog.has_schema_privilege(oid, 'USAGE') FROM pg_catalog.pg_namespace WHERE nspname = $2), "
        + "pg_catalog.set_config('search_path', $1, false), "
        + "pg_catalog.current_schema(), "
        + "pg_catalog.current_setting('standard_conforming_strings')";

    /// <summary>The key of <see cref="Exception.Data"/> that holds where a non-default search_path is set.</summary>
    internal const string DefaultPathSourcesKey = "PgDbRoleSetting";

    private static readonly Action<ILogger, string, Exception?> LogDefaultPath = LoggerMessage.Define<string>(
        LogLevel.Error,
        new EventId(1, "TenantDefaultSearchPath"),
        "Tenant connections are refused: a new session's search_path is not PostgreSQL's default. Set by: {Sources}");

    // Verdicts of connection strings already checked (the connection string never holds the password). Bounded.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string?> SafetyVerdicts = new(StringComparer.Ordinal);
    private static int _safetyVerdicts;

    // Where a default search_path comes from, when the session's is not PostgreSQL's: role and database settings.
    private const string DefaultSourcesSql =
        "SELECT pg_catalog.string_agg(CASE "
        + "WHEN s.setdatabase = 0 THEN 'ALTER ROLE ' || pg_catalog.quote_ident(r.rolname) "
        + "WHEN s.setrole = 0 THEN 'ALTER DATABASE ' || pg_catalog.quote_ident(d.datname) "
        + "ELSE 'ALTER ROLE ' || pg_catalog.quote_ident(r.rolname) || ' IN DATABASE ' || pg_catalog.quote_ident(d.datname) END "
        + "|| ' SET search_path', '; '), "
        + "pg_catalog.string_agg(DISTINCT CASE WHEN s.setdatabase = 0 THEN 'ALTER ROLE' WHEN s.setrole = 0 THEN 'ALTER DATABASE' "
        + "ELSE 'ALTER ROLE ... IN DATABASE' END || ' ... SET search_path', '; ') "
        + "FROM pg_catalog.pg_db_role_setting s "
        + "LEFT JOIN pg_catalog.pg_roles r ON r.oid = s.setrole LEFT JOIN pg_catalog.pg_database d ON d.oid = s.setdatabase "
        + "WHERE s.setdatabase IN (0, (SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database())) "
        + "AND s.setrole IN (0, (SELECT oid FROM pg_catalog.pg_roles WHERE rolname = session_user)) "
        + "AND EXISTS (SELECT 1 FROM pg_catalog.unnest(s.setconfig) c WHERE c LIKE 'search_path=%')";

    // Per connection object, shared by every guard, so contexts of one scope can share a connection (and a
    // transaction): each guard still requires the binding to be its own tenant's. Not tenant state: an entry only says
    // which schema a guard bound the connection to, and any change of state clears it.
    private static readonly ConditionalWeakTable<DbConnection, Binding> Bindings = [];

    private volatile bool _optionsVerified;

    /// <summary>Whether <paramref name="command"/> is the statement that binds a connection, which is not traced.</summary>
    internal static bool IsBindingCommand(NpgsqlCommand command) => command is BindingCommand;

    /// <summary>Throws when an ambient transaction is active: no tenant connection is opened or handed out inside one.</summary>
    internal static void EnsureNoAmbientTransaction()
    {
        if (Transaction.Current is not null)
        {
            throw new InvalidOperationException(AmbientTransactionMessage);
        }
    }

    /// <summary>Whether this guard has bound <paramref name="connection"/> to the current tenant.</summary>
    internal bool IsBound(DbConnection? connection) =>
        connection is not null
        && Bindings.TryGetValue(connection, out var binding)
        && binding.Schema is { } schema
        && currentTenant.IsSet
        && string.Equals(schema, currentTenant.SchemaName, StringComparison.Ordinal);

    public DbConnection ConnectionCreated(ConnectionCreatedEventData eventData, DbConnection result)
    {
        _ = Track(result);
        return result;
    }

    public InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData eventData, InterceptionResult result)
    {
        BeginOpen(connection, eventData);
        return result;
    }

    public ValueTask<InterceptionResult> ConnectionOpeningAsync(
        DbConnection connection, ConnectionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
    {
        BeginOpen(connection, eventData);
        return ValueTask.FromResult(result);
    }

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        var binding = Track(connection);
        binding.Opening = false;
        var schema = currentTenant.SchemaName;
        try
        {
            BindResult row;
            using (var command = new BindingCommand(AsNpgsql(connection), TenantSchemaName.SearchPathFor(schema)))
            using (var reader = command.ExecuteReader())
            {
                row = reader.Read() ? BindResult.From(reader) : default;
            }

            if (row.DefaultProblem() is { } problem)
            {
                using var sources = new NpgsqlCommand(DefaultSourcesSql, AsNpgsql(connection));
                using var found = sources.ExecuteReader();
                _ = found.Read();
                throw DefaultPathException(problem, found.IsDBNull(0) ? null : found.GetString(0), found.IsDBNull(1) ? null : found.GetString(1), eventData.Context);
            }

            row.Verify(schema);
        }
        catch
        {
            CloseQuietly(connection);
            throw;
        }

        binding.Schema = schema;
    }

    public async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var binding = Track(connection);
        binding.Opening = false;
        var schema = currentTenant.SchemaName;
        try
        {
            BindResult row;
            await using (var command = new BindingCommand(AsNpgsql(connection), TenantSchemaName.SearchPathFor(schema)))
            await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                row = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? BindResult.From(reader) : default;
            }

            if (row.DefaultProblem() is { } problem)
            {
                await using var sources = new NpgsqlCommand(DefaultSourcesSql, AsNpgsql(connection));
                await using var found = await sources.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                _ = await found.ReadAsync(cancellationToken).ConfigureAwait(false);
                throw DefaultPathException(problem, found.IsDBNull(0) ? null : found.GetString(0), found.IsDBNull(1) ? null : found.GetString(1), eventData.Context);
            }

            row.Verify(schema);
        }
        catch
        {
            await CloseQuietlyAsync(connection).ConfigureAwait(false);
            throw;
        }

        binding.Schema = schema;
    }

    public void ConnectionFailed(DbConnection connection, ConnectionErrorEventData eventData) => Track(connection).Opening = false;

    public Task ConnectionFailedAsync(DbConnection connection, ConnectionErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        Track(connection).Opening = false;
        return Task.CompletedTask;
    }

    public InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnsureAllowed(command);
        return result;
    }

    public ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(command);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnsureAllowed(command);
        return result;
    }

    public ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(command);
        return ValueTask.FromResult(result);
    }

    public InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnsureAllowed(command);
        return result;
    }

    public ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        EnsureAllowed(command);
        return ValueTask.FromResult(result);
    }

    // The message names the kind of setting but no role or database: the names go to the log event and to Exception.Data,
    // for operators.
    private static InvalidOperationException DefaultPathException(string problem, string? sources, string? kinds, DbContext? context)
    {
        const string Elsewhere = "no role or database setting; look at the connection's startup options, PGOPTIONS, the role chosen "
            + "by PGUSER, or the server configuration";
        var where = string.IsNullOrEmpty(sources) ? Elsewhere : sources + "; remove it with the same ALTER statement and RESET search_path";
        var exception = new InvalidOperationException(
            $"{problem} After a rollback or a reset, a tenant connection would fall back to it. Set by: "
            + (string.IsNullOrEmpty(kinds) ? Elsewhere : kinds + " (pg_db_role_setting; the role and database names are in the error log)")
            + ". The connection is closed.");
        exception.Data[DefaultPathSourcesKey] = where;
        if (context?.GetInfrastructure().GetService<ILoggerFactory>() is { } loggers)
        {
            LogDefaultPath(loggers.CreateLogger<TenantConnectionGuard>(), where, null);
        }

        return exception;
    }

    private static NpgsqlConnection AsNpgsql(DbConnection connection) =>
        connection as NpgsqlConnection ?? throw new InvalidOperationException("A tenant context must run on an Npgsql connection.");

    // Closing returns the physical connection to the pool, whose reset clears whatever the failed statement left.
    // A failure to close must not hide the original exception.
    private static void CloseQuietly(DbConnection connection)
    {
        try
        {
            connection.Close();
        }
        catch (DbException)
        {
            // The connection is broken; it will not be reused.
        }
    }

    private static async Task CloseQuietlyAsync(DbConnection connection)
    {
        try
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
        catch (DbException)
        {
            // The connection is broken; it will not be reused.
        }
    }

    private static Binding Track(DbConnection connection) =>
        Bindings.GetValue(connection, static created =>
        {
            var binding = new Binding();
            created.StateChange += binding.OnStateChange;
            return binding;
        });

    private void BeginOpen(DbConnection connection, ConnectionEventData eventData)
    {
        // Validates before anything is sent: a tenant, a valid schema name, no ambient transaction.
        _ = currentTenant.IsSet ? TenantSchemaName.Validate(currentTenant.SchemaName) : throw new InvalidOperationException(NoTenantMessage);
        EnsureNoAmbientTransaction();
        EnsureSafeSettings(AsNpgsql(connection));
        if (!_optionsVerified)
        {
            VerifyOptions(eventData.Context);
            _optionsVerified = true;
        }

        Track(connection).Opening = true;
    }

    // Every open, whatever connection object the context holds now (SetDbConnection can swap it): its settings must be safe.
    // The verdict is cached per connection string.
    private static void EnsureSafeSettings(NpgsqlConnection connection)
    {
        var text = connection.ConnectionString;
        if (!SafetyVerdicts.TryGetValue(text, out var refusal))
        {
            refusal = ConnectionSafety.Refusal(new NpgsqlConnectionStringBuilder(text));
            if (Volatile.Read(ref _safetyVerdicts) < 1024 && SafetyVerdicts.TryAdd(text, refusal))
            {
                Interlocked.Increment(ref _safetyVerdicts);
            }
        }

        if (refusal is not null)
        {
            throw new InvalidOperationException($"The connection of this tenant context {refusal}");
        }
    }

    // What the building block guarantees must still hold, whatever ran after UseTenantPersistence.
    private void VerifyOptions(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        var options = context.GetService<IDbContextOptions>();
        var relational = RelationalOptionsExtension.Extract(options);
        if (!string.Equals(relational.MigrationsHistoryTableName, historyTable, StringComparison.Ordinal)
            || relational.MigrationsHistoryTableSchema is not null)
        {
            throw new InvalidOperationException(
                $"The options of {context.GetType().FullName} were changed after UseTenantPersistence: its history table must be "
                + $"'{historyTable}', unqualified. Configure further options before the tenant configuration, never after it.");
        }

        if (options.FindExtension<CoreOptionsExtension>()?.Interceptors?.Contains(this) != true)
        {
            throw new InvalidOperationException(
                $"The options of {context.GetType().FullName} no longer hold the tenant guard.");
        }
    }

    private void EnsureAllowed(DbCommand command)
    {
        if (!IsBound(command.Connection))
        {
            throw new InvalidOperationException(UnboundMessage);
        }

        if (SessionStatements.Refusal(command.CommandText) is { } statement)
        {
            throw new InvalidOperationException(
                $"The command is refused on a tenant context: it contains {statement}, which would change the session's search "
                + "path or role after the connection was bound to the tenant. Tenant SQL never changes the session.");
        }
    }

    // What the bind statement returned.
    private readonly record struct BindResult(
        string? DefaultPath, bool UserSchemaExists, bool? ExtensionsUsable, string? Path, string? Schema, string? ConformingStrings)
    {
        public static BindResult From(DbDataReader reader) => new(
            reader.IsDBNull(0) ? null : reader.GetString(0),
            !reader.IsDBNull(1) && reader.GetBoolean(1),
            reader.IsDBNull(2) ? null : reader.GetBoolean(2),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetString(4),
            reader.IsDBNull(5) ? null : reader.GetString(5));

        public string? DefaultProblem() =>
            !string.Equals(DefaultPath, DefaultSearchPath, StringComparison.Ordinal)
                ? $"A new session's search_path is not PostgreSQL's default ({DefaultSearchPath})."
                : null;

        public void Verify(string schema)
        {
            if (!string.Equals(ConformingStrings, "on", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "standard_conforming_strings is off on this server, database or role: the statement check reads SQL with it "
                    + "on, so tenant connections are refused. Turn it back on (PostgreSQL's default). The connection is closed.");
            }

            if (UserSchemaExists)
            {
                throw new InvalidOperationException(
                    "A schema is named like the database user, so \"$user\" in the default search_path resolves to it, and after a "
                    + "rollback or a reset a tenant connection would fall back to it. Rename or drop that schema, or connect with "
                    + "another role. The connection is closed.");
            }

            if (ExtensionsUsable == false)
            {
                throw new InvalidOperationException(
                    $"The database role cannot use the schema '{TenantSchemaName.ExtensionsSchema}' (no USAGE), so the extensions "
                    + "there would be silently invisible. Grant it (see TenantMigratorOptions.ApplicationRole). The connection is closed.");
            }

            if (!string.Equals(Path, TenantSchemaName.SearchPathFor(schema), StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The tenant's search_path was not set on the connection; the connection is closed.");
            }

            if (!string.Equals(Schema, schema, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The tenant's schema does not exist or cannot be used (the tenant is not provisioned); the connection is closed.");
            }
        }
    }

    // The state of one connection object. Any change of state clears the binding; only a guard sets it, after the open
    // it started itself.
    private sealed class Binding
    {
        public bool Opening { get; set; }

        public string? Schema { get; set; }

        public void OnStateChange(object? sender, StateChangeEventArgs args)
        {
            Schema = null;
            if (args.CurrentState == ConnectionState.Open && !Opening)
            {
                throw new InvalidOperationException(ForeignOpenMessage);
            }

            if (args.CurrentState != ConnectionState.Open)
            {
                Opening = false;
            }
        }
    }

    // The type the tracing filter recognizes; no other code creates one.
    private sealed class BindingCommand : NpgsqlCommand
    {
        public BindingCommand(NpgsqlConnection connection, string searchPath)
            : base(BindSql, connection)
        {
            Parameters.Add(new NpgsqlParameter { Value = searchPath });
            Parameters.Add(new NpgsqlParameter { Value = TenantSchemaName.ExtensionsSchema });
        }
    }
}
