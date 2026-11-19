using MyApp.BuildingBlocks.Application.Tenancy;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Infrastructure.Persistence;

/// <summary>
/// The <see cref="ISchemaProvider"/> the security library's context requires. The model of <see cref="AccessDbContext"/>
/// names no tenant schema, so its value never reaches SQL; it returns the current tenant's schema and throws without a
/// tenant, so nothing can fall back to a shared schema.
/// </summary>
/// <param name="currentTenant">The tenant of the scope.</param>
public sealed class AccessSchemaProvider(ICurrentTenant currentTenant) : ISchemaProvider
{
    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The scope has no tenant.</exception>
    public string GetSchemaName() => currentTenant.SchemaName;

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">The scope has no tenant.</exception>
    public string GetDefaultSchema() => currentTenant.SchemaName;
}
