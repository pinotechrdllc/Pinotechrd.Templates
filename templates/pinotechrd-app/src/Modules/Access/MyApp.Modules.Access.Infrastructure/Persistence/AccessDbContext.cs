using Microsoft.EntityFrameworkCore;
using Pinotechrd.Security.Data;
using Pinotechrd.Security.Entities;
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

    /// <summary>The unique index on the token hash: a duplicate save violates it.</summary>
    public const string RefreshTokenHashIndex = "ix_refresh_tokens_token_hash";

    /// <summary>Gets the refresh token families (sign-in sessions) of the tenant.</summary>
    public DbSet<RefreshTokenFamily> RefreshTokenFamilies => Set<RefreshTokenFamily>();

    /// <summary>Gets the refresh tokens of the tenant, stored as hashes.</summary>
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

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

    /// <summary>
    /// The library's model, plus the integrity its migrations would otherwise lack: a role's permissions and a user's
    /// overrides reference their role and user (deleted with them), and each pair is unique. The library replaces those
    /// sets by removing the old rows and adding distinct new ones in one save, which the unique indexes allow: EF Core
    /// deletes before it inserts a row with the same key values. The refresh token tables are added: a token references
    /// its family (deleted with it) and its hash is unique. Every table stays without a schema.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<RolePermission>(entity =>
        {
            entity.HasOne<AccessRole>().WithMany().HasForeignKey(e => e.RoleId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.RoleId, e.PermissionId }).IsUnique();
        });

        modelBuilder.Entity<UserModulePermission>(entity =>
        {
            entity.HasOne<AccessUser>().WithMany().HasForeignKey(e => e.UserId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => new { e.UserId, e.PermissionId }).IsUnique();
        });

        // The refresh token store's tables (DatabaseRefreshTokenStore), also without a schema and without a tenant column.
        modelBuilder.Entity<RefreshTokenFamily>(entity =>
        {
            entity.ToTable("refresh_token_families");
            entity.Property(e => e.Id).ValueGeneratedNever();
            // Revoking every session of a user filters on it.
            entity.HasIndex(e => e.UserId);
        });

        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.ToTable("refresh_tokens");
            entity.Property(e => e.Id).ValueGeneratedNever();
            entity.HasOne<RefreshTokenFamily>().WithMany().HasForeignKey(e => e.FamilyId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(e => e.TokenHash).IsUnique().HasDatabaseName(RefreshTokenHashIndex);
        });
    }
}
