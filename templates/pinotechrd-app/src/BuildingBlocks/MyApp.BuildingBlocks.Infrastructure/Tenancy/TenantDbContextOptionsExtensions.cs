using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Application.Tenancy;
using Npgsql;
using Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// The tenant behaviour of a context, for any place that builds its options: <see cref="TenantPersistenceRegistration.AddTenantDbContext{TContext}"/>
/// uses it, and so can a context configured elsewhere (a library's context, or contexts built outside dependency injection
/// by a message framework). Also the one supported way to run raw SQL on a tenant context.
/// </summary>
public static class TenantDbContextOptionsExtensions
{
    /// <summary>
    /// Makes the context a tenant context of the module <paramref name="moduleName"/>: Npgsql on
    /// <paramref name="dataSource"/> (checked: no <c>Multiplexing</c>, no <c>No Reset On Close</c>, no search path), the
    /// module's unqualified history table (<see cref="TenantPersistenceRegistration.HistoryTableOf"/>), snake_case names,
    /// and the guard that binds each connection to <paramref name="currentTenant"/> and refuses commands on a connection it
    /// did not bind. The model must name no schema.
    /// </summary>
    /// <param name="options">The options builder of the context.</param>
    /// <param name="dataSource">The shared data source.</param>
    /// <param name="currentTenant">The current tenant of the scope the context lives in.</param>
    /// <param name="moduleName">The module name, a PascalCase identifier of ASCII letters and digits.</param>
    /// <param name="npgsqlOptions">
    /// Further Npgsql options, applied before the history table, which they cannot change. Never give a schema.
    /// </param>
    /// <param name="allowPrivilegedLogin">
    /// Whether the guard accepts a superuser or <c>BYPASSRLS</c> login (<see cref="TenancyOptions.AllowPrivilegedLogin"/>);
    /// <see langword="false"/> by default.
    /// </param>
    /// <returns>The same options builder, for chaining.</returns>
    /// <exception cref="ArgumentException">The module name is not valid.</exception>
    /// <exception cref="InvalidOperationException">The data source sets a refused setting.</exception>
    public static DbContextOptionsBuilder UseTenantPersistence(
        this DbContextOptionsBuilder options,
        NpgsqlDataSource dataSource,
        ICurrentTenant currentTenant,
        string moduleName,
        Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptions = null,
        bool allowPrivilegedLogin = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(currentTenant);
        var historyTable = TenantPersistenceRegistration.HistoryTableOf(moduleName);

        ConfigureProvider(options, TenantDataSource.Check(dataSource), historyTable, npgsqlOptions);
        return options.AddInterceptors(new TenantConnectionGuard(currentTenant, historyTable, allowPrivilegedLogin));
    }

    /// <summary>
    /// The same, with the data source, the current tenant and <see cref="TenancyOptions"/> (when registered) taken from
    /// <paramref name="scopedServices"/>, the services of the scope the context lives in.
    /// </summary>
    /// <param name="options">The options builder of the context.</param>
    /// <param name="scopedServices">The services of the context's scope.</param>
    /// <param name="moduleName">The module name.</param>
    /// <param name="npgsqlOptions">Further Npgsql options; never a schema.</param>
    /// <returns>The same options builder, for chaining.</returns>
    public static DbContextOptionsBuilder UseTenantPersistence(
        this DbContextOptionsBuilder options,
        IServiceProvider scopedServices,
        string moduleName,
        Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);

        return options.UseTenantPersistence(
            scopedServices.GetRequiredService<NpgsqlDataSource>(),
            scopedServices.GetRequiredService<ICurrentTenant>(),
            moduleName,
            npgsqlOptions,
            scopedServices.GetService<IOptions<TenancyOptions>>()?.Value.AllowPrivilegedLogin ?? false);
    }

    /// <summary>
    /// Opens the connection of a tenant context through EF Core, so it is bound to the current tenant, and returns it for
    /// raw SQL (Dapper, an <see cref="NpgsqlCommand"/>). This is the one supported way: opening
    /// <c>Database.GetDbConnection()</c> yourself throws, and the context then refuses its own commands on that
    /// connection. The context owns the connection: it stays open until <c>Database.CloseConnectionAsync()</c> or the end
    /// of the scope; do not dispose it. Commands run directly on it are outside EF Core and get no statement check: they
    /// must never change the session (<c>SET search_path</c>, <c>RESET</c>, <c>DISCARD ALL</c>, <c>SET ROLE</c>,
    /// <c>set_config('search_path', ...)</c>). The statement check on EF's own commands is a guard against mistakes, not
    /// a security boundary (see the README). Refused inside an ambient <c>TransactionScope</c>. Holding this connection
    /// for the request also avoids paying the bind for every query.
    /// </summary>
    /// <param name="database">The context's database facade.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <returns>The open connection, with <c>search_path</c> set to the current tenant.</returns>
    /// <exception cref="InvalidOperationException">The context is not a tenant context, or there is no current tenant.</exception>
    public static async Task<NpgsqlConnection> OpenTenantConnectionAsync(this DatabaseFacade database, CancellationToken cancellationToken = default)
    {
        var guard = GuardOf(database);
        await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return BoundConnection(database, guard);
    }

    /// <summary>The synchronous form of <see cref="OpenTenantConnectionAsync"/>.</summary>
    /// <param name="database">The context's database facade.</param>
    /// <returns>The open connection, with <c>search_path</c> set to the current tenant.</returns>
    public static NpgsqlConnection OpenTenantConnection(this DatabaseFacade database)
    {
        var guard = GuardOf(database);
        database.OpenConnection();
        return BoundConnection(database, guard);
    }

    /// <summary>The provider configuration shared by run time and design time: no schema, so search_path places everything.</summary>
    internal static DbContextOptionsBuilder ConfigureProvider(
        DbContextOptionsBuilder options, NpgsqlDataSource? dataSource, string historyTable, Action<NpgsqlDbContextOptionsBuilder>? npgsqlOptions = null)
    {
        void Npgsql(NpgsqlDbContextOptionsBuilder npgsql)
        {
            npgsqlOptions?.Invoke(npgsql);
            npgsql.MigrationsHistoryTable(historyTable);
        }

        if (dataSource is null)
        {
            options.UseNpgsql(Npgsql);
        }
        else
        {
            options.UseNpgsql(dataSource, Npgsql);
        }

        return options.UseSnakeCaseNamingConvention();
    }

    private static TenantConnectionGuard GuardOf(DatabaseFacade database)
    {
        ArgumentNullException.ThrowIfNull(database);
        TenantConnectionGuard.EnsureNoAmbientTransaction();

        var interceptors = database.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>()?.Interceptors;
        return interceptors?.OfType<TenantConnectionGuard>().FirstOrDefault()
            ?? throw new InvalidOperationException("The context is not a tenant context (it was not configured with UseTenantPersistence).");
    }

    private static NpgsqlConnection BoundConnection(DatabaseFacade database, TenantConnectionGuard guard)
    {
        var connection = (NpgsqlConnection)database.GetDbConnection();
        return guard.IsBound(connection)
            ? connection
            : throw new InvalidOperationException(TenantConnectionGuard.UnboundMessage);
    }
}
