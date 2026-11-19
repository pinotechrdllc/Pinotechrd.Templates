using Microsoft.EntityFrameworkCore.Design;
using MyApp.BuildingBlocks.Infrastructure.Migrations;

namespace MyApp.Modules.Access.Infrastructure.Persistence.Catalog;

/// <summary>
/// The design-time factory of <see cref="CatalogDbContext"/> for <c>dotnet ef migrations add</c>: the run-time provider,
/// naming and history table, with no connection.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    /// <inheritdoc />
    public CatalogDbContext CreateDbContext(string[] args) =>
        new(SharedPersistenceRegistration.DesignTimeOptions<CatalogDbContext>(CatalogDbContext.Schema));
}
