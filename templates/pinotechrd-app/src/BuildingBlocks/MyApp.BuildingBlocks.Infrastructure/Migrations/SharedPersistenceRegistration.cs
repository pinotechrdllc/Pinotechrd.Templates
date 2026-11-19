using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// Shared persistence: EF Core contexts that are not tenant-scoped (the catalog of tenants and plans), each in one schema
/// of its own on the shared <see cref="NpgsqlDataSource"/>, and the database migrator that migrates them before the
/// tenants.
/// <para>
/// The rules every shared context follows: every table, view, sequence and function in its one schema (set it with
/// <c>HasDefaultSchema</c>); snake_case names; and a history table of its own, <see cref="HistoryTableOf"/>, in that
/// schema. It has no tenant guard: it never sees a tenant schema, and a tenant context never sees it.
/// </para>
/// </summary>
public static class SharedPersistenceRegistration
{
    // PostgreSQL truncates identifiers longer than this, in bytes; the names here are ASCII.
    private const int MaxIdentifierLength = 63;

    /// <summary>
    /// Registers <typeparamref name="TContext"/> as a scoped shared context in <paramref name="schema"/>: Npgsql on the
    /// shared data source (checked like a tenant's: no <c>Multiplexing</c>, no <c>No Reset On Close</c>, no search path),
    /// snake_case names and the history table <see cref="HistoryTableOf"/> in that schema. It also registers the
    /// context's <see cref="ISharedMigrations"/> for the database migrator, which refuses a model with an object outside
    /// that schema. The context's migrations are read from its own assembly. Registering the same context again with the
    /// same schema has no further effect.
    /// </summary>
    /// <typeparam name="TContext">The shared context; its model puts everything in <paramref name="schema"/>.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="schema">The context's schema; see <see cref="HistoryTableOf"/> for the names accepted.</param>
    /// <param name="configure">Further options, applied before the shared configuration, which they cannot replace.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentException">The schema is not valid.</exception>
    /// <exception cref="InvalidOperationException">The context or the schema is already registered with another one.</exception>
    public static IServiceCollection AddSharedDbContext<TContext>(
        this IServiceCollection services, string schema, Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var historyTable = HistoryTableOf(schema);

        foreach (var registered in services
                     .Where(d => d.ServiceType == typeof(ISharedMigrations) && !d.IsKeyedService)
                     .Select(d => d.ImplementationInstance)
                     .OfType<ISharedMigrations>())
        {
            var sameContext = registered is SharedMigrations<TContext>;
            var sameSchema = string.Equals(registered.Schema, schema, StringComparison.Ordinal);
            if (sameContext && sameSchema)
            {
                return services;
            }

            if (sameContext || sameSchema)
            {
                throw new InvalidOperationException(
                    $"The schema '{schema}' and the context {typeof(TContext).FullName} cannot be registered together: the schema "
                    + $"'{registered.Schema}' already has a context of that type or name. Each shared schema has one context.");
            }
        }

        services.AddDbContext<TContext>((provider, options) =>
        {
            configure?.Invoke(provider, options);
            ConfigureProvider(options, TenantDataSource.Check(provider.GetRequiredService<NpgsqlDataSource>()), schema, historyTable);
        });
        services.AddSingleton<ISharedMigrations>(new SharedMigrations<TContext>(schema));
        return services;
    }

    /// <summary>
    /// Declares that the application's login reads <paramref name="columns"/> of <paramref name="schema"/>.<paramref name="table"/>
    /// directly, through a shared context (see <see cref="ApplicationRead"/>). The database migrator grants exactly that on
    /// every run, to <c>Migrator:ApplicationRole</c>; with no application role the application connects with the
    /// migrator's own login, which owns the tables, and nothing is granted. Declaring the same table again adds its columns.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="schema">The table's schema, a shared schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="columns">The columns, at least one; every name matches <see cref="TenantSchemaName.Pattern"/>.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentException">A name is not valid, or no column is given.</exception>
    public static IServiceCollection AddApplicationRead(this IServiceCollection services, string schema, string table, params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(services);

        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length == 0)
        {
            throw new ArgumentException("An application read names at least one column.", nameof(columns));
        }

        services.AddSingleton(new ApplicationRead(schema, table, columns));
        return services;
    }

    /// <summary>
    /// Declares that the application's login inserts rows into <paramref name="schema"/>.<paramref name="table"/> directly,
    /// giving values for <paramref name="columns"/> only (see <see cref="ApplicationRead"/>): the database migrator grants
    /// <c>INSERT</c> on those columns on every run, to <c>Migrator:ApplicationRole</c>, and never <c>UPDATE</c> or
    /// <c>DELETE</c>. An identity column filled by the database needs no grant to be filled; reading it back (an insert
    /// that returns the new id) needs it declared with <see cref="AddApplicationRead"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="schema">The table's schema, a shared schema.</param>
    /// <param name="table">The table.</param>
    /// <param name="columns">The columns, at least one; every name matches <see cref="TenantSchemaName.Pattern"/>.</param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentException">A name is not valid, or no column is given.</exception>
    public static IServiceCollection AddApplicationAppend(this IServiceCollection services, string schema, string table, params string[] columns)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Length == 0)
        {
            throw new ArgumentException("An application append names at least one column.", nameof(columns));
        }

        services.AddSingleton(new ApplicationRead(schema, table, [], columns));
        return services;
    }

    /// <summary>
    /// Registers <see cref="IDatabaseMigrator"/>, and the <see cref="ITenantMigrator"/> it uses for the tenants. Only the
    /// migrator command registers it: migrations never run at API start. It also adds to the database's start-time
    /// validation a check that a pooled connection string allows at least <see cref="DatabaseMigrator.MinimumPoolSize"/>
    /// connections.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets <see cref="TenantMigratorOptions"/>, such as a separate application role.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDatabaseMigrator(this IServiceCollection services, Action<TenantMigratorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTenantMigrator(configure);
        // With AddDatabase's start-time validation, a pool smaller than the migrator needs stops the host at start.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, MigratorPoolSizeValidator>());
        services.TryAddSingleton<IDatabaseMigrator>(provider => new DatabaseMigrator(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<TenantDataSource>(),
            provider.GetServices<ISharedMigrations>(),
            provider.GetRequiredService<ITenantMigrator>(),
            provider.GetServices<ApplicationRead>(),
            provider.GetRequiredService<IOptions<TenantMigratorOptions>>().Value.ApplicationRole));
        return services;
    }

    /// <summary>
    /// Options for the design-time tools (<c>dotnet ef migrations add</c>, through an <c>IDesignTimeDbContextFactory</c>):
    /// the same provider, naming and history table as at run time, with no connection and no data source. Never use them
    /// to reach a database.
    /// </summary>
    /// <typeparam name="TContext">The shared context.</typeparam>
    /// <param name="schema">The context's schema.</param>
    /// <returns>The options.</returns>
    public static DbContextOptions<TContext> DesignTimeOptions<TContext>(string schema)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>();
        ConfigureProvider(options, dataSource: null, schema, HistoryTableOf(schema));
        return options.Options;
    }

    /// <summary>
    /// Gets the history table of the shared context in <paramref name="schema"/>:
    /// <see cref="TenantPersistenceRegistration.HistoryTablePrefix"/> followed by the schema (<c>catalog</c> gives
    /// <c>__ef_migrations_catalog</c>), in that schema.
    /// </summary>
    /// <param name="schema">
    /// The schema: it matches <see cref="TenantSchemaName.Pattern"/>, and is not <c>public</c>, a <c>pg_</c> schema, the
    /// extensions schema or a tenant schema (<see cref="TenantSchemaName.Prefix"/>).
    /// </param>
    /// <returns>The table name.</returns>
    /// <exception cref="ArgumentException">The schema is not valid, or makes a table name longer than 63 characters.</exception>
    public static string HistoryTableOf(string schema)
    {
        TenantSchemaName.Validate(schema);
        if (schema is "public" or TenantSchemaName.ExtensionsSchema
            || schema.StartsWith("pg_", StringComparison.Ordinal)
            || schema.StartsWith(TenantSchemaName.Prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"The schema '{schema}' cannot hold a shared context: public, pg_ schemas, '{TenantSchemaName.ExtensionsSchema}' "
                + $"and tenant schemas ('{TenantSchemaName.Prefix}...') are refused.",
                nameof(schema));
        }

        var table = TenantPersistenceRegistration.HistoryTablePrefix + schema;
        return table.Length <= MaxIdentifierLength
            ? table
            : throw new ArgumentException(
                $"The schema '{schema}' is too long: its history table would exceed {MaxIdentifierLength} characters.", nameof(schema));
    }

    private static void ConfigureProvider(DbContextOptionsBuilder options, NpgsqlDataSource? dataSource, string schema, string historyTable)
    {
        if (dataSource is null)
        {
            options.UseNpgsql(npgsql => npgsql.MigrationsHistoryTable(historyTable, schema));
        }
        else
        {
            options.UseNpgsql(dataSource, npgsql => npgsql.MigrationsHistoryTable(historyTable, schema));
        }

        options.UseSnakeCaseNamingConvention();
    }
}
