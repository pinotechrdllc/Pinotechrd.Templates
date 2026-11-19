using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Provisioning;
using MyApp.Migrator.Tests.Support;

namespace MyApp.Migrator.Tests.Bootstrap;

/// <summary>What a database holds after bootstrap, read with plain SQL.</summary>
internal static class BootstrapSupport
{
    /// <summary>A database whose catalog the migrate command has brought up to date.</summary>
    public static async Task<string> MigratedDatabaseAsync(PostgresFixture postgres)
    {
        var database = await postgres.CreateDatabaseAsync();
        await MigrateAsync(database);
        return database;
    }

    public static async Task MigrateAsync(string database, params string[] extra)
    {
        var run = await MigratorRun.ExecuteAsync(database, extra);
        Assert.True(run.ExitCode == 0, run.Report);
    }

    public static async Task<List<TenantRow>> OperatorTenantsAsync(string database) =>
        await ProvisionSupport.RowsAsync(database, BootstrapRun.DefaultSlug);

    public static string Schema(Guid tenant) => TenantSchemaName.For(tenant);

    /// <summary>The identifier of the tenant of the bootstrap, once it exists.</summary>
    public static async Task<Guid> TenantAsync(string database) => Assert.Single(await OperatorTenantsAsync(database)).Id;

    /// <summary>"plan code|tenant_name|customer_name" of every subscription a tenant row of the slug points to.</summary>
    public static Task<List<string>> SubscriptionsAsync(string database) =>
        Db.ColumnAsync(
            database,
            "SELECT p.code || '|' || coalesce(s.tenant_name, '') || '|' || s.customer_name FROM catalog.tenants t "
            + "JOIN catalog.system_subscriptions s ON s.id = t.subscription_id JOIN catalog.subscription_plans p ON p.id = s.plan_id "
            + $"WHERE t.identifier = '{BootstrapRun.DefaultSlug}'");

    public static Task<long> OperatorSubscriptionCountAsync(string database) =>
        Db.CountAsync(
            database,
            "SELECT count(*) FROM catalog.system_subscriptions s JOIN catalog.subscription_plans p ON p.id = s.plan_id WHERE p.code = 'OPERATOR'");

    public static Task<List<string>> UsersAsync(string database, Guid tenant) =>
        Db.ColumnAsync(database, $"SELECT email || '|' || user_name || '|' || email_confirmed FROM \"{Schema(tenant)}\".users ORDER BY email");

    public static async Task<string> PasswordHashAsync(string database, Guid tenant) =>
        Assert.Single(await Db.ColumnAsync(database, $"SELECT password_hash FROM \"{Schema(tenant)}\".users"));

    public static Task<List<string>> AdministratorRolesAsync(string database, Guid tenant) =>
        Db.ColumnAsync(database, $"SELECT id || '|' || name FROM \"{Schema(tenant)}\".roles ORDER BY name");

    public static Task<List<string>> AdministratorPermissionsAsync(string database, Guid tenant) =>
        Db.ColumnAsync(
            database,
            $"SELECT p.code FROM \"{Schema(tenant)}\".roles r JOIN \"{Schema(tenant)}\".role_permissions rp ON rp.role_id = r.id "
            + $"JOIN \"{Schema(tenant)}\".security_permissions p ON p.id = rp.permission_id WHERE r.name = 'Administrator' ORDER BY p.code COLLATE \"C\"");

    public static Task<long> MembershipCountAsync(string database, Guid tenant) =>
        Db.CountAsync(
            database,
            $"SELECT count(*) FROM \"{Schema(tenant)}\".user_roles ur JOIN \"{Schema(tenant)}\".roles r ON r.id = ur.role_id WHERE r.name = 'Administrator'");

    /// <summary>The state a second run must leave alone, as one comparable list.</summary>
    public static async Task<List<string>> SnapshotAsync(string database, Guid tenant)
    {
        var snapshot = new List<string>();
        snapshot.AddRange(await Db.ColumnAsync(database, $"SELECT 'user|' || id || '|' || coalesce(password_hash, '') || '|' || coalesce(security_stamp, '') FROM \"{Schema(tenant)}\".users"));
        snapshot.AddRange(await AdministratorRolesAsync(database, tenant));
        snapshot.AddRange(await Db.ColumnAsync(database, "SELECT 'tenant|' || id || '|' || identifier || '|' || subscription_id || '|' || status FROM catalog.tenants"));
        snapshot.AddRange(await Db.ColumnAsync(database, "SELECT 'subscription|' || id || '|' || plan_id || '|' || coalesce(tenant_name, '') FROM catalog.system_subscriptions ORDER BY id"));
        snapshot.AddRange(await AdministratorPermissionsAsync(database, tenant));
        snapshot.Add($"memberships|{await MembershipCountAsync(database, tenant)}");
        return snapshot;
    }
}
