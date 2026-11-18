using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// What a module contributes to the tenant migrator: its name and its context. It lives in the infrastructure building
/// block, not next to the web module contract, so a migrator that enumerates the modules' migrations does not depend on
/// ASP.NET Core. <see cref="TenantPersistenceRegistration.AddTenantDbContext{TContext}"/> registers one for each context;
/// the migrator receives every registered instance.
/// </summary>
public interface IModuleMigrations
{
    /// <summary>
    /// Gets the module name, a PascalCase identifier of ASCII letters and digits such as <c>Sales</c>. It orders the
    /// migration of a tenant (<c>Access</c> first, then by name) and names the history table
    /// (<see cref="TenantPersistenceRegistration.HistoryTableOf"/>).
    /// </summary>
    string ModuleName { get; }

    /// <summary>
    /// Gets the module's context from <paramref name="scopedServices"/>, the services of a scope whose tenant is set. The
    /// scope owns the context and disposes it.
    /// </summary>
    /// <param name="scopedServices">The services of the scope.</param>
    /// <returns>The context.</returns>
    DbContext GetContext(IServiceProvider scopedServices);
}

/// <summary>The migrations of the module that owns <typeparamref name="TContext"/>.</summary>
/// <typeparam name="TContext">The module's context.</typeparam>
internal sealed class ModuleMigrations<TContext>(string moduleName) : IModuleMigrations
    where TContext : DbContext
{
    public string ModuleName { get; } = moduleName;

    public DbContext GetContext(IServiceProvider scopedServices) => scopedServices.GetRequiredService<TContext>();
}
