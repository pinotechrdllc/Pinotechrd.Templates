using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Application.Tenancy;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// Tenant persistence: EF Core contexts on the one shared <see cref="NpgsqlDataSource"/> that see only the current
/// tenant's schema, and the migrator that applies each module's migrations to a tenant's schema.
/// <para>
/// The rules every tenant context follows: no schema anywhere in its model or its migrations (no
/// <c>HasDefaultSchema</c>, no schema in <c>ToTable</c>), because the schema is chosen per connection by
/// <c>search_path</c> and one model serves every tenant; snake_case names; and a migration history of its own,
/// <see cref="HistoryTableOf"/>, unqualified, so it lands in the tenant's schema. The data source comes from
/// <see cref="Database.DatabaseRegistration.AddDatabase"/> (or any registered <see cref="NpgsqlDataSource"/>); tenant
/// contexts never build a pool of their own.
/// </para>
/// </summary>
public static class TenantPersistenceRegistration
{
    /// <summary>The prefix of every module's migration history table.</summary>
    public const string HistoryTablePrefix = "__ef_migrations_";

    // PostgreSQL truncates identifiers longer than this, in bytes; the names here are ASCII.
    private const int MaxIdentifierLength = 63;

    /// <summary>
    /// Registers the scoped <see cref="ICurrentTenant"/> and <see cref="ICurrentTenantInitializer"/> (one object per
    /// scope), the check of the shared data source and <see cref="TenancyOptions"/>, read from
    /// <see cref="TenancyOptions.AllowPrivilegedLoginKey"/> when the host has a configuration. Called by the other methods of this class; a host that only reads
    /// or sets the tenant calls it directly. Registering twice has no further effect.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTenantPersistence(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (!services.Any(d => d.ServiceType == typeof(CurrentTenant)))
        {
            // The configuration is optional: a host without one keeps the default (privileged logins refused).
            services.AddOptions<TenancyOptions>().Configure<IServiceProvider>((options, provider) =>
            {
                var value = provider.GetService<IConfiguration>()?[TenancyOptions.AllowPrivilegedLoginKey];
                if (!string.IsNullOrWhiteSpace(value))
                {
                    options.AllowPrivilegedLogin = bool.TryParse(value, out var allowed)
                        ? allowed
                        : throw new InvalidOperationException($"{TenancyOptions.AllowPrivilegedLoginKey} must be true or false.");
                }
            });
        }

        services.TryAddScoped<CurrentTenant>();
        services.TryAddScoped<ICurrentTenant>(sp => sp.GetRequiredService<CurrentTenant>());
        services.TryAddScoped<ICurrentTenantInitializer>(sp => sp.GetRequiredService<CurrentTenant>());
        services.TryAddSingleton<TenantDataSource>();
        services.TryAddSingleton<ITenantJobRunner, TenantJobRunner>();
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TContext"/> as a scoped context of the module <paramref name="moduleName"/>: on the
    /// shared data source, configured by <see cref="TenantDbContextOptionsExtensions.UseTenantPersistence(DbContextOptionsBuilder, IServiceProvider, string, System.Action{Npgsql.EntityFrameworkCore.PostgreSQL.Infrastructure.NpgsqlDbContextOptionsBuilder})"/>
    /// (snake_case names, its history table <see cref="HistoryTableOf"/>, and the guard that binds every connection it
    /// opens to the current tenant, refuses to open one without a tenant and refuses commands on a connection it did not
    /// bind). It also registers the module's <see cref="IModuleMigrations"/> for the migrator. The context's migrations are
    /// read from its own assembly.
    /// <para>
    /// Resolve the context from a scope whose tenant is set (or set before the first query). Tenant contexts are never
    /// pooled (<c>AddDbContextPool</c>): a pooled context would keep the tenant of its first scope. Registering the same context again with the
    /// same module name has no further effect.
    /// </para>
    /// </summary>
    /// <typeparam name="TContext">The module's context. Its model names no schema.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="moduleName">The module name, a PascalCase identifier of ASCII letters and digits such as <c>Sales</c>.</param>
    /// <param name="configure">
    /// Further options (logging, warnings, more interceptors, Npgsql options). It runs before the tenant configuration,
    /// which then sets the data source, the history table, the naming and the guard over whatever it set.
    /// </param>
    /// <returns>The same service collection, for chaining.</returns>
    /// <exception cref="ArgumentException">The module name is not valid (see <see cref="HistoryTableOf"/>).</exception>
    /// <exception cref="InvalidOperationException">
    /// The context is already registered for another module, or another context is registered for this module.
    /// </exception>
    public static IServiceCollection AddTenantDbContext<TContext>(
        this IServiceCollection services, string moduleName, Action<IServiceProvider, DbContextOptionsBuilder>? configure = null)
        where TContext : DbContext
    {
        ArgumentNullException.ThrowIfNull(services);
        var historyTable = HistoryTableOf(moduleName);

        foreach (var registered in services
                     .Where(d => d.ServiceType == typeof(IModuleMigrations) && !d.IsKeyedService)
                     .Select(d => d.ImplementationInstance)
                     .OfType<IModuleMigrations>())
        {
            var sameContext = registered is ModuleMigrations<TContext>;
            // Compared by history table: two spellings of one name (Sales, SALES...) would share a history table.
            var sameModule = string.Equals(HistoryTableOf(registered.ModuleName), historyTable, StringComparison.Ordinal);
            if (sameContext && sameModule)
            {
                return services;
            }

            if (sameContext || sameModule)
            {
                throw new InvalidOperationException(
                    $"The module '{moduleName}' and the context {typeof(TContext).FullName} cannot be registered together: "
                    + $"the module '{registered.ModuleName}' already has a context of that type or name. Each module has one context.");
            }
        }

        services.AddTenantPersistence();
        services.AddDbContext<TContext>((provider, options) =>
        {
            // The callback first: the tenant configuration comes last, so nothing the callback sets can replace the data
            // source, the history table, the naming or the guard; the guard checks them again at the first open.
            configure?.Invoke(provider, options);
            options.UseTenantPersistence(
                provider.GetRequiredService<TenantDataSource>().Value,
                provider.GetRequiredService<ICurrentTenant>(),
                moduleName,
                allowPrivilegedLogin: provider.GetRequiredService<IOptions<TenancyOptions>>().Value.AllowPrivilegedLogin);
        });
        services.AddSingleton<IModuleMigrations>(new ModuleMigrations<TContext>(moduleName));
        return services;
    }

    /// <summary>
    /// Registers <see cref="ITenantMigrator"/>, which migrates tenant schemas with every registered
    /// <see cref="IModuleMigrations"/>. Only the migrator command registers it: migrations never run at API start.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets <see cref="TenantMigratorOptions"/>, such as a separate application role.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTenantMigrator(this IServiceCollection services, Action<TenantMigratorOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddTenantPersistence();
        var options = services.AddOptions<TenantMigratorOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        services.TryAddSingleton<ITenantMigrator, TenantMigrator>();
        return services;
    }

    /// <summary>
    /// Registers <typeparamref name="TSeeder"/> (a singleton that reads scoped services from the scope it is given) as an
    /// <see cref="ITenantSeeder"/>: the tenant migrator runs it for every tenant after the modules' migrations, under the
    /// tenant's lock. Seeders run in registration order. Registering the same type again has no further effect.
    /// </summary>
    /// <typeparam name="TSeeder">The seeder.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTenantSeeder<TSeeder>(this IServiceCollection services)
        where TSeeder : class, ITenantSeeder
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddEnumerable(ServiceDescriptor.Singleton<ITenantSeeder, TSeeder>());
        return services;
    }

    /// <summary>
    /// Options for the design-time tools (<c>dotnet ef migrations add</c>, through an <c>IDesignTimeDbContextFactory</c>
    /// in the module): the same provider, naming and history table as at run time, with no connection, no data source
    /// and no interceptor. Never use them to reach a database.
    /// </summary>
    /// <typeparam name="TContext">The module's context.</typeparam>
    /// <param name="moduleName">The module name.</param>
    /// <returns>The options.</returns>
    public static DbContextOptions<TContext> DesignTimeOptions<TContext>(string moduleName)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>();
        TenantDbContextOptionsExtensions.ConfigureProvider(options, dataSource: null, HistoryTableOf(moduleName));
        return options.Options;
    }

    /// <summary>
    /// Gets the migration history table of a module: <see cref="HistoryTablePrefix"/> and the module name in snake case,
    /// with an underscore where a new word starts (the same word rule as the module's route segment). <c>Access</c> gives
    /// <c>__ef_migrations_access</c>, <c>SalesOrders</c> <c>__ef_migrations_sales_orders</c>, <c>HRPayroll</c>
    /// <c>__ef_migrations_hr_payroll</c>. The table is never schema-qualified.
    /// </summary>
    /// <param name="moduleName">The module name: ASCII letters and digits, starting with a letter.</param>
    /// <returns>The table name.</returns>
    /// <exception cref="ArgumentException">The name is empty, has another character, or makes a table name longer than 63 characters.</exception>
    public static string HistoryTableOf(string moduleName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (!char.IsAsciiLetter(moduleName[0]) || !moduleName.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException(
                $"The module name '{moduleName}' is not valid: module names use ASCII letters and digits and start with a letter.",
                nameof(moduleName));
        }

        var table = new StringBuilder(HistoryTablePrefix, HistoryTablePrefix.Length + moduleName.Length + 4);
        for (var i = 0; i < moduleName.Length; i++)
        {
            var current = moduleName[i];
            if (i > 0 && char.IsAsciiLetterUpper(current) && StartsWord(moduleName[i - 1], i + 1 < moduleName.Length ? moduleName[i + 1] : '\0'))
            {
                table.Append('_');
            }

            table.Append(char.ToLowerInvariant(current));
        }

        if (table.Length > MaxIdentifierLength)
        {
            throw new ArgumentException(
                $"The module name '{moduleName}' is too long: its history table would exceed {MaxIdentifierLength} characters.",
                nameof(moduleName));
        }

        return table.ToString();
    }

    private static bool StartsWord(char previous, char next) =>
        char.IsAsciiLetterLower(previous) || char.IsAsciiDigit(previous)
        || (char.IsAsciiLetterUpper(previous) && char.IsAsciiLetterLower(next));
}
