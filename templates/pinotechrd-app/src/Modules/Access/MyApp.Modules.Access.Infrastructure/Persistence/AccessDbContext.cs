using Microsoft.EntityFrameworkCore;
using Pinotechrd.Security.Data;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Infrastructure.Persistence;

/// <summary>
/// The tenant context of the Access module: the security library's model (users, roles, permissions, per-user allow and
/// deny) with no schema on the tenant tables, so the tenant persistence guard selects the schema per connection, and the
/// catalog tables (tenants, plans, subscriptions) in the <see cref="CatalogSchema"/> schema, excluded from its
/// migrations. Registered by the module with <c>AddTenantDbContext</c> and <see cref="ConfigureModel"/>, never on a data
/// source or a connection of its own.
/// </summary>
/// <param name="options">The options built by the tenant persistence registration.</param>
/// <param name="schemaProvider">The schema provider the library requires; see <see cref="AccessSchemaProvider"/>.</param>
public sealed class AccessDbContext(DbContextOptions<AccessDbContext> options, ISchemaProvider schemaProvider)
    : PinotechrdSecurityDbContext<AccessUser, AccessRole>(options, schemaProvider)
{
    /// <summary>The shared schema of the catalog tables, the same for every tenant.</summary>
    public const string CatalogSchema = "catalog";

    /// <summary>
    /// The model settings of the security library for this context: tenant tables found through <c>search_path</c>,
    /// catalog tables in <see cref="CatalogSchema"/> and outside its migrations, and none of the library's
    /// <c>search_path</c> interceptors, because the tenant persistence guard binds every connection.
    /// </summary>
    /// <param name="options">The options builder of the context.</param>
    /// <returns>The same options builder, for chaining.</returns>
    public static DbContextOptionsBuilder ConfigureModel(DbContextOptionsBuilder options) =>
        options.UsePinotechrdSecurity(static model =>
        {
            model.TenantSchemaMode = TenantSchemaMode.SearchPath;
            model.CatalogSchema = CatalogSchema;
            model.ExcludeCatalogFromMigrations = true;
            model.ApplicationTakesResponsibilityForSearchPath = true;
        });
}
