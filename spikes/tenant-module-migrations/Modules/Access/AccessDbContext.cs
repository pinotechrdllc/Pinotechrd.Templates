using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TenantModuleMigrations.Modules.Access;

public sealed class User
{
    public long Id { get; set; }
    public string Email { get; set; } = "";
    public string? DisplayName { get; set; }
}

public sealed class Role
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
}

/// <summary>The Access module owns only its own tables. No schema is set: the connection decides it.</summary>
public sealed class AccessDbContext(DbContextOptions<AccessDbContext> options) : DbContext(options)
{
    public const string HistoryTable = "__ef_migrations_access";

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>().ToTable("access_users");
        modelBuilder.Entity<Role>().ToTable("access_roles");
    }

    public static AccessDbContext Create(string connectionString, string schema, SchemaMode mode = SchemaMode.ConnectionString) =>
        new(Tenancy.Options<AccessDbContext>(connectionString, schema, HistoryTable, mode));
}

public sealed class AccessDbContextFactory : IDesignTimeDbContextFactory<AccessDbContext>
{
    public AccessDbContext CreateDbContext(string[] args) => AccessDbContext.Create("Host=localhost", "design_time");
}
