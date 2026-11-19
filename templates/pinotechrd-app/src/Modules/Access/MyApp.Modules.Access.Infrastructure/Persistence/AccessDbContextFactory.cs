using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Infrastructure.Persistence;

/// <summary>
/// The design-time factory of <see cref="AccessDbContext"/> for <c>dotnet ef migrations add</c>: the run-time provider,
/// naming, history table and model settings, with no connection and no tenant. Its schema provider throws: in
/// <c>SearchPath</c> mode the library never asks for a schema, so the model it builds names none.
/// </summary>
public sealed class AccessDbContextFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    /// <inheritdoc />
    public AccessDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AccessDbContext>(
            TenantPersistenceRegistration.DesignTimeOptions<AccessDbContext>(AccessPersistenceRegistration.ModuleName));
        AccessDbContext.ConfigureModel(options);
        return new AccessDbContext(options.Options, new NoTenantSchemaProvider());
    }

    private sealed class NoTenantSchemaProvider : ISchemaProvider
    {
        public string GetSchemaName() => throw NoTenant();

        public string GetDefaultSchema() => throw NoTenant();

        private static InvalidOperationException NoTenant() =>
            new("The design-time Access context has no tenant: its model names no tenant schema.");
    }
}
