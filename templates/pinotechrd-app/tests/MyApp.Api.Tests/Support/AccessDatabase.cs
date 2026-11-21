using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Npgsql;
using Pinotechrd.Security.Core;
using Pinotechrd.Security.Data;
using Pinotechrd.Security.Entities;
using Pinotechrd.Security.Enums;
using Pinotechrd.Security.Interfaces;
using Pinotechrd.Security.Tenancy;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// A PostgreSQL database shaped like the one the Access module runs on: one schema per tenant holding the module's tenant
/// tables, and the shared <c>catalog</c> schema holding tenants, plans and subscriptions. The migrations are not part of
/// this unit, so the tables come from the create script of the module's own context (the tenant tables, the same for every
/// tenant, no schema in the script) and, for the catalog tables the module's context leaves out of its migrations, of a
/// test-only context. Rows are added through the application's own services, in a scope whose tenant is set.
/// </summary>
internal static class AccessDatabase
{
    /// <summary>The module row every tenant of a test database has: the plans of the catalog name it by this id, the library's id of its code.</summary>
    public static readonly Guid AccessModuleId = SecurityModuleId.FromCode("ACCESS");

    public const string ReadCode = "ACCESS.USERS.READ";

    public const string DefaultPassword = "Passw0rd!-Test-91";

    /// <summary>A new database in the shared container whose tenants have their tables, and the catalog schema its own.</summary>
    public static async Task<string> CreateAsync(PostgresFixture postgres, params Guid[] tenants)
    {
        Assert.NotEmpty(tenants);
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await RunAsync(connection, $"CREATE SCHEMA IF NOT EXISTS \"{TenantSchemaName.ExtensionsSchema}\"");
        // The catalog tables are only mapped for creation by the test-only context, whose script also repeats the tenant tables
        // without the module's own tables (the refresh token ones). It runs in a Scratch schema that is dropped at once: only
        // the catalog tables, which the script names with their schema, stay. Every tenant then gets the module's own script.
        const string Scratch = "catalog_seed_scratch";
        await RunAsync(connection, $"CREATE SCHEMA IF NOT EXISTS \"{Scratch}\"; SET search_path TO \"{Scratch}\"");
        await RunAsync(connection, ScriptOf<CatalogAndTenantContext>(excludeCatalog: false));
        await RunAsync(connection, $"DROP SCHEMA \"{Scratch}\" CASCADE");
        foreach (var tenant in tenants)
        {
            await RunAsync(connection, SetSearchPath(tenant));
            await RunAsync(connection, ScriptOf<AccessDbContext>(excludeCatalog: true));
        }

        // What the migrator gives each tenant: its role, with the grants of the tenant's tables and of the shared schemas.
        foreach (var tenant in tenants)
        {
            await RunAsync(connection, TenantRoleSql.For(connectionString, tenant));
        }

        return connectionString;
    }

    /// <summary>Runs <paramref name="work"/> in a scope of the host whose tenant is set, as a request would have it.</summary>
    public static async Task InTenantAsync(IServiceProvider services, Guid tenant, Func<IServiceProvider, Task> work)
    {
        await using var scope = services.CreateAsyncScope();
        EnterTenant(scope.ServiceProvider, tenant);
        await work(scope.ServiceProvider);
    }

    /// <summary>Sets the tenant of the scope for the application (<c>ICurrentTenant</c>) and for the security library.</summary>
    public static void EnterTenant(IServiceProvider scope, Guid tenant)
    {
        scope.GetRequiredService<ICurrentTenantInitializer>().Initialize(tenant);
        scope.GetRequiredService<TenantContext>().SetTenant(tenant.ToString("D"));
    }

    /// <summary>
    /// Gives each tenant an active subscription of one plan that includes the module, in the catalog, and the module with
    /// the permission <see cref="ReadCode"/> in the tenant's own tables (the catalog names the module by its id).
    /// </summary>
    public static async Task SeedPlanAsync(IServiceProvider services, params Guid[] tenants)
    {
        var plan = SubscriptionPlan.Create("Plan", "PLAN-" + Guid.NewGuid().ToString("N")[..8], null, null);
        // Tenant roles only read the shared catalog (that is what the tests prove elsewhere): the catalog is filled with
        // the write right lent to the shared role for the length of the seeding, as an operator's own tooling would.
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        var sharedRole = TenantRoles.SharedOf(dataSource.ConnectionString);
        await RunOnAsync(dataSource, $"GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA \"{AccessDbContext.CatalogSchema}\" TO \"{sharedRole}\"");
        await InTenantAsync(services, tenants[0], async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            db.SubscriptionPlans.Add(plan);
            db.SubscriptionPlanModules.Add(SubscriptionPlanModule.Create(plan.Id, AccessModuleId));
            foreach (var tenant in tenants)
            {
                db.SystemSubscriptions.Add(SystemSubscription.Create(
                    "Customer " + tenant.ToString("N")[..6], tenant.ToString("D"), plan.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1))));
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        await RunOnAsync(dataSource, $"REVOKE INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA \"{AccessDbContext.CatalogSchema}\" FROM \"{sharedRole}\"");

        foreach (var tenant in tenants)
        {
            await InTenantAsync(services, tenant, async scope =>
            {
                var db = scope.GetRequiredService<AccessDbContext>();
                var module = new SecurityModule { Id = AccessModuleId, Name = "Access", Code = "ACCESS", CreatedAt = DateTime.UtcNow };
                db.SecurityModules.Add(module);
                db.SecurityPermissions.Add(SecurityPermission.Create(AccessModuleId, "Read users", ReadCode));
                await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            });
        }
    }

    /// <summary>
    /// Adds the catalog row of each tenant (its slug and status), on the first subscription of the catalog: the row a
    /// request's slug is looked up in and whose status decides whether the tenant may be used. Call after <see cref="SeedPlanAsync"/>.
    /// </summary>
    public static async Task SeedTenantRowsAsync(IServiceProvider services, params (Guid Id, string Slug, TenantStatus Status)[] tenants)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        var sharedRole = TenantRoles.SharedOf(dataSource.ConnectionString);
        await RunOnAsync(dataSource, $"GRANT INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA \"{AccessDbContext.CatalogSchema}\" TO \"{sharedRole}\"");
        await InTenantAsync(services, tenants[0].Id, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var subscription = await db.SystemSubscriptions.Select(s => s.Id).FirstAsync(TestContext.Current.CancellationToken);
            foreach (var (id, slug, status) in tenants)
            {
                db.Tenants.Add(new Tenant
                {
                    Id = id,
                    Identifier = slug,
                    Name = "Tenant " + slug,
                    SubscriptionId = subscription,
                    DbSchema = TenantSchemaName.For(id),
                    Status = status,
                    CreatedAt = DateTime.UtcNow,
                });
            }

            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });
        await RunOnAsync(dataSource, $"REVOKE INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA \"{AccessDbContext.CatalogSchema}\" FROM \"{sharedRole}\"");
    }

    /// <summary>Changes the status of the tenant's catalog row, as an operator would; the running host sees it on the next request.</summary>
    public static async Task SetTenantStatusAsync(IServiceProvider services, Guid tenant, TenantStatus status)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"UPDATE \"{AccessDbContext.CatalogSchema}\".tenants SET status = @status WHERE id = @id";
        command.Parameters.AddWithValue("status", (int)status);
        command.Parameters.AddWithValue("id", tenant);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>Adds a user of <paramref name="tenant"/> with a password; returns its id.</summary>
    public static async Task<Guid> AddUserAsync(IServiceProvider services, Guid tenant, string email)
    {
        var id = Guid.Empty;
        await InTenantAsync(services, tenant, async scope =>
        {
            var users = scope.GetRequiredService<UserManager<AccessUser>>();
            var user = new AccessUser { UserName = email, Email = email };
            var result = await users.CreateAsync(user, DefaultPassword);
            Assert.True(result.Succeeded, string.Join("; ", result.Errors.Select(e => e.Description)));
            id = user.Id;
        });
        return id;
    }

    /// <summary>Sets the user's lockout end, as time passing (or an operator) would: Identity reads the clock, so a test moves the end instead.</summary>
    public static Task SetLockoutEndAsync(IServiceProvider services, Guid tenant, Guid userId, DateTimeOffset? end) =>
        InTenantAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
            user.LockoutEnd = end;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>Sets whether the user is active.</summary>
    public static Task SetUserActiveAsync(IServiceProvider services, Guid tenant, Guid userId, bool active) =>
        InTenantAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var user = await db.Users.SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
            user.IsActive = active;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>What the users table holds about the user's security state.</summary>
    public static async Task<(string? SecurityStamp, int AccessFailedCount, DateTimeOffset? LockoutEnd)> SecurityStateAsync(
        IServiceProvider services, Guid tenant, Guid userId)
    {
        (string?, int, DateTimeOffset?) state = default;
        await InTenantAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
            state = (user.SecurityStamp, user.AccessFailedCount, user.LockoutEnd);
        });
        return state;
    }

    /// <summary>Makes the user a member of a new role that grants <see cref="ReadCode"/>.</summary>
    public static Task GrantReadThroughARoleAsync(IServiceProvider services, Guid tenant, Guid userId) =>
        InTenantAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var role = new AccessRole { Id = Guid.NewGuid(), Name = "Reader-" + Guid.NewGuid().ToString("N")[..6] };
            role.NormalizedName = role.Name.ToUpperInvariant();
            db.Roles.Add(role);
            var permission = await db.SecurityPermissions.SingleAsync(p => p.Code == ReadCode, TestContext.Current.CancellationToken);
            db.RolePermissions.Add(RolePermission.Create(role.Id, permission.Id));
            db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = userId, RoleId = role.Id });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    /// <summary>Gives the user a per-user allow or deny of <see cref="ReadCode"/>.</summary>
    public static Task OverrideReadAsync(IServiceProvider services, Guid tenant, Guid userId, PermissionAccessType access) =>
        InTenantAsync(services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var permission = await db.SecurityPermissions.SingleAsync(p => p.Code == ReadCode, TestContext.Current.CancellationToken);
            db.UserModulePermissions.Add(access == PermissionAccessType.Allow
                ? UserModulePermission.CreateAllow(userId, permission.Id)
                : UserModulePermission.CreateDeny(userId, permission.Id));
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    private static async Task RunOnAsync(NpgsqlDataSource dataSource, string sql)
    {
        await using var connection = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await RunAsync(connection, sql);
    }

    private static string SetSearchPath(Guid tenant)
    {
        var schema = TenantSchemaName.For(tenant);
        return $"CREATE SCHEMA IF NOT EXISTS \"{schema}\"; SET search_path TO \"{schema}\"";
    }

    private static async Task RunAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
#pragma warning disable CA2100 // Generated by EF Core from the model, or built from a GUID.
        command.CommandText = sql;
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static string ScriptOf<TContext>(bool excludeCatalog)
        where TContext : PinotechrdSecurityDbContext<AccessUser, AccessRole>
    {
        var builder = new DbContextOptionsBuilder<TContext>(TenantPersistenceRegistration.DesignTimeOptions<TContext>("Access"));
        if (typeof(TContext) == typeof(AccessDbContext))
        {
            AccessDbContext.ConfigureModel(builder);
        }
        else
        {
            builder.UsePinotechrdSecurity(model =>
            {
                model.TenantSchemaMode = TenantSchemaMode.SearchPath;
                model.CatalogSchema = AccessDbContext.CatalogSchema;
                model.ExcludeCatalogFromMigrations = excludeCatalog;
                model.ApplicationTakesResponsibilityForSearchPath = true;
            });
        }

        using var context = (TContext)Activator.CreateInstance(typeof(TContext), builder.Options, new FixedSchema())!;
        return context.Database.GenerateCreateScript();
    }

    // Only the model is built to write the script: no query reaches this name.
    private sealed class FixedSchema : ISchemaProvider
    {
        public string GetSchemaName() => "unused";

        public string GetDefaultSchema() => "unused";
    }

    /// <summary>The module's own model, with the catalog tables left in the script.</summary>
    internal sealed class CatalogAndTenantContext(DbContextOptions<CatalogAndTenantContext> options, ISchemaProvider schemaProvider)
        : PinotechrdSecurityDbContext<AccessUser, AccessRole>(options, schemaProvider);
}
