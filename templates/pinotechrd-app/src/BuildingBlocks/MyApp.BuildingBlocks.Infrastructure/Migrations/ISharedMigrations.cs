using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MyApp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// What a shared context (one that is not tenant-scoped, such as the catalog) contributes to the database migrator: its
/// schema and its context. <see cref="SharedPersistenceRegistration.AddSharedDbContext{TContext}"/> registers one for
/// each context; the migrator migrates them in registration order, before any tenant.
/// </summary>
public interface ISharedMigrations
{
    /// <summary>Gets the one schema that holds every table of the context and its history table.</summary>
    string Schema { get; }

    /// <summary>Gets the context from <paramref name="scopedServices"/>; the scope owns the context and disposes it.</summary>
    /// <param name="scopedServices">The services of a scope.</param>
    /// <returns>The context.</returns>
    DbContext GetContext(IServiceProvider scopedServices);
}

/// <summary>The migrations of the shared context <typeparamref name="TContext"/>.</summary>
/// <typeparam name="TContext">The shared context.</typeparam>
internal sealed class SharedMigrations<TContext>(string schema) : ISharedMigrations
    where TContext : DbContext
{
    public string Schema { get; } = schema;

    public DbContext GetContext(IServiceProvider scopedServices) => scopedServices.GetRequiredService<TContext>();
}
