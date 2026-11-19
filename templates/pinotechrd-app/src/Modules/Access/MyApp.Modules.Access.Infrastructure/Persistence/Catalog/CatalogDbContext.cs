using Microsoft.EntityFrameworkCore;
using Pinotechrd.Security.Entities;

namespace MyApp.Modules.Access.Infrastructure.Persistence.Catalog;

/// <summary>
/// The shared catalog context: the security library's five catalog tables (tenants, subscription plans, system
/// subscriptions, plan modules, plan features) in the schema <see cref="Schema"/>, the one context that migrates them.
/// <see cref="AccessDbContext"/> maps the same tables and excludes them from its own migrations; both mappings must stay
/// equal (same tables, columns, types and nullability as the library's <c>PinotechrdSecurityDbContext</c>), which a test
/// checks against the migrated catalog. It is a plain context, not derived from the library's, so it carries no Identity
/// table. Registered with <c>AddSharedDbContext</c>, history table <c>catalog.__ef_migrations_catalog</c>.
/// </summary>
/// <param name="options">The options built by the shared persistence registration.</param>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    /// <summary>The catalog schema, the same one <see cref="AccessDbContext"/> reads.</summary>
    public const string Schema = AccessDbContext.CatalogSchema;

    /// <summary>Gets the tenants.</summary>
    public DbSet<Tenant> Tenants => Set<Tenant>();

    /// <summary>Gets the subscription plans.</summary>
    public DbSet<SubscriptionPlan> SubscriptionPlans => Set<SubscriptionPlan>();

    /// <summary>Gets the system subscriptions.</summary>
    public DbSet<SystemSubscription> SystemSubscriptions => Set<SystemSubscription>();

    /// <summary>Gets the plan-module mappings.</summary>
    public DbSet<SubscriptionPlanModule> SubscriptionPlanModules => Set<SubscriptionPlanModule>();

    /// <summary>Gets the plan features.</summary>
    public DbSet<SubscriptionPlanFeature> SubscriptionPlanFeatures => Set<SubscriptionPlanFeature>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        // The same settings as the library's catalog mapping (PinotechrdSecurityDbContext.OnModelCreating).
        modelBuilder.HasDefaultSchema(Schema);

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.Property(e => e.Name).HasMaxLength(200).IsRequired();
            entity.Property(e => e.Identifier).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.Identifier).IsUnique();
            entity.HasOne(e => e.Subscription).WithMany(s => s.Tenants).HasForeignKey(e => e.SubscriptionId);
        });

        modelBuilder.Entity<SubscriptionPlan>(entity =>
        {
            entity.ToTable("subscription_plans");
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Code).HasMaxLength(50).IsRequired();
            entity.Property(e => e.MonthlyPrice).HasPrecision(18, 2);
            entity.HasIndex(e => e.Code).IsUnique();
        });

        modelBuilder.Entity<SystemSubscription>(entity =>
        {
            entity.ToTable("system_subscriptions");
            entity.HasOne(e => e.Plan).WithMany(p => p.SystemSubscriptions).HasForeignKey(e => e.PlanId);
        });

        modelBuilder.Entity<SubscriptionPlanModule>(entity =>
        {
            entity.ToTable("subscription_plan_modules");
            entity.HasOne(e => e.Plan).WithMany(p => p.PlanModules).HasForeignKey(e => e.PlanId);
            // Modules live in each tenant's schema: matched by ModuleId, never a foreign key from the catalog.
            entity.Ignore(e => e.Module);
            entity.HasIndex(e => e.ModuleId);
        });

        modelBuilder.Entity<SubscriptionPlanFeature>(entity =>
        {
            entity.ToTable("subscription_plan_features");
            entity.Property(e => e.Code).HasMaxLength(100).IsRequired();
            entity.Property(e => e.Value).HasMaxLength(500);
            entity.Property(e => e.Description).HasMaxLength(500);
            entity.HasOne(e => e.Plan).WithMany(p => p.PlanFeatures).HasForeignKey(e => e.PlanId);
        });
    }
}
