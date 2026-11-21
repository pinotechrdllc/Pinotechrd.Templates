using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using MyApp.BuildingBlocks.Domain;
using MyApp.BuildingBlocks.Infrastructure.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Application.Tenants;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;
using MyApp.Modules.Access.Infrastructure.Seeding;

namespace MyApp.Migrator;

/// <summary>
/// The <c>bootstrap</c> command (ADR 0013): once the catalog is up to date, the plans; the operator's subscription
/// (<see cref="SeedIds.OperatorSubscription"/>, plan <see cref="PlanComposition.OperatorPlanCode"/>); the operator tenant,
/// provisioned like any tenant; the subscription linked to it (<c>tenant_name</c> = the tenant's id, the security
/// package's convention); then the tenant migrated again with the <see cref="OperatorAdminSeeder"/> targeting it, which
/// creates the <c>Administrator</c> role and the first administrator. Every step is idempotent. The whole flow runs under
/// the session advisory lock <see cref="LockKey"/>, so concurrent runs take turns: the check that the operator
/// subscription holds no tenant of another slug and the provisioning that follows it are one step for every other run.
/// The report never holds the email or the password.
/// </summary>
internal static class MigratorBootstrap
{
    /// <summary>The key of the session advisory lock that serializes bootstrap runs.</summary>
    internal const string LockKey = "operator-bootstrap";

    /// <summary>Runs the command.</summary>
    /// <param name="services">The host's services.</param>
    /// <param name="options">The validated settings.</param>
    /// <param name="report">Where the report is written.</param>
    /// <param name="cancellationToken">Stops the run; what finished stays.</param>
    /// <returns>The exit code.</returns>
    public static async Task<int> RunAsync(
        IServiceProvider services, BootstrapOptions options, TextWriter report, CancellationToken cancellationToken)
    {
        try
        {
            var lockConnection = await AdvisoryLock.AcquireAsync(services.GetRequiredService<NpgsqlDataSource>(), LockKey, cancellationToken).ConfigureAwait(false);
            await using (lockConnection.ConfigureAwait(false))
            {
                try
                {
                    var scope = services.CreateAsyncScope();
                    await using (scope.ConfigureAwait(false))
                    {
                        return await RunInScopeAsync(scope.ServiceProvider, options, report, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    await AdvisoryLock.ReleaseAsync(lockConnection, LockKey).ConfigureAwait(false);
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await report.WriteLineAsync($"Bootstrap failed: {MigratorHost.Describe(exception)}").ConfigureAwait(false);
            return MigratorHost.MigrationFailed;
        }
        catch (OperationCanceledException)
        {
            await report.WriteLineAsync("Bootstrap cancelled: what finished stays; run the same command again to complete it.").ConfigureAwait(false);
            return MigratorHost.Cancelled;
        }
    }

    private static async Task<int> RunInScopeAsync(
        IServiceProvider services, BootstrapOptions options, TextWriter report, CancellationToken cancellationToken)
    {
        var catalog = services.GetRequiredService<CatalogDbContext>();
        if ((await catalog.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
        {
            await report.WriteLineAsync("Bootstrap failed: the catalog is not up to date. Run the migrator without a command (migrate) first.")
                .ConfigureAwait(false);
            return MigratorHost.MigrationFailed;
        }

        // Without a password nothing can be created: refused before any write when the tenant does not exist yet.
        var slug = options.Slug!;
        if (options.AdminPassword is null
            && await TenantIdAsync(catalog, slug, cancellationToken).ConfigureAwait(false) is null)
        {
            await report.WriteLineAsync($"Invalid settings: {PasswordRequired}").ConfigureAwait(false);
            return MigratorHost.InvalidSettings;
        }

        var plans = await services.GetRequiredService<CatalogPlanSeeder>().SeedAsync(cancellationToken).ConfigureAwait(false);
        await report.WriteLineAsync(MigratorHost.Invariant($"Plans: {plans.PlansAdded} plan(s) and {plans.PlanModulesAdded} plan module(s) added."))
            .ConfigureAwait(false);

        var subscriptionId = SeedIds.OperatorSubscription;
        await EnsureSubscriptionAsync(catalog, subscriptionId, options.Name!, cancellationToken).ConfigureAwait(false);
        if (!await SubscriptionFreeForAsync(catalog, subscriptionId, slug, cancellationToken).ConfigureAwait(false))
        {
            await report.WriteLineAsync(LinkedElsewhere).ConfigureAwait(false);
            return MigratorHost.MigrationFailed;
        }

        var provisioned = await services.GetRequiredService<TenantProvisioner>()
            .ProvisionOperatorAsync(new ProvisionTenantRequest(slug, options.Name!, subscriptionId), cancellationToken)
            .ConfigureAwait(false);
        if (provisioned.IsFailure)
        {
            // Error messages never quote the request's values.
            await report.WriteLineAsync($"Bootstrap failed: {provisioned.Error.Code}: {provisioned.Error.Message}").ConfigureAwait(false);
            return provisioned.Error.Kind == ErrorKind.Validation ? MigratorHost.InvalidSettings : MigratorHost.MigrationFailed;
        }

        var tenantId = provisioned.Value.TenantId;
        await report.WriteLineAsync(MigratorHost.Invariant(
                $"Tenant {tenantId} ({slug}): {(provisioned.Value.AlreadyProvisioned ? "already provisioned" : "provisioned")}."))
            .ConfigureAwait(false);

        if (!await LinkAsync(catalog, subscriptionId, tenantId, cancellationToken).ConfigureAwait(false))
        {
            await report.WriteLineAsync(LinkedElsewhere).ConfigureAwait(false);
            return MigratorHost.MigrationFailed;
        }

        var target = services.GetRequiredService<BootstrapTarget>();
        target.Set(tenantId, options.AdminEmail!, options.AdminPassword);
        var migration = await services.GetRequiredService<ITenantMigrator>().MigrateAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (!migration.Succeeded)
        {
            await report.WriteLineAsync(MigratorHost.Invariant($"Bootstrap failed: tenant {tenantId}: {MigratorHost.Describe(migration.Error!)}"))
                .ConfigureAwait(false);
            return MigratorHost.MigrationFailed;
        }

        return await ReportAdministratorAsync(target, report).ConfigureAwait(false);
    }

    // TenantStatus.Active of the security package, as the catalog stores it. In SQL, like every catalog access here: the
    // migrator references no type of the package (the Access infrastructure owns them).
    private const int ActiveStatus = 1;

    private const string PasswordRequired =
        BootstrapOptions.AdminPasswordKey + " is required to create the administrator; set it in the environment (Bootstrap__AdminPassword) or in user secrets.";

    private const string LinkedElsewhere =
        "Bootstrap failed: the operator subscription belongs to another tenant; bootstrap the tenant it belongs to (the same Bootstrap:Slug).";

    private static async Task<int> ReportAdministratorAsync(BootstrapTarget target, TextWriter report)
    {
        switch (target.Outcome)
        {
            case BootstrapAdminOutcome.Created:
                await report.WriteLineAsync("Administrator: created.").ConfigureAwait(false);
                return MigratorHost.Success;
            case BootstrapAdminOutcome.AlreadyPresent:
                await report.WriteLineAsync("Administrator: already present, unchanged.").ConfigureAwait(false);
                return MigratorHost.Success;
            case BootstrapAdminOutcome.PasswordRequired:
                await report.WriteLineAsync($"Invalid settings: {PasswordRequired}").ConfigureAwait(false);
                return MigratorHost.InvalidSettings;
            case BootstrapAdminOutcome.AnotherAdministrator:
                await report.WriteLineAsync(
                        "Bootstrap failed: the operator tenant already has an administrator with another email; set Bootstrap:AdminEmail to that administrator's email.")
                    .ConfigureAwait(false);
                return MigratorHost.MigrationFailed;
            case BootstrapAdminOutcome.Failed:
                // Identity's codes only: its descriptions can quote the user name.
                await report.WriteLineAsync($"Bootstrap failed: the administrator was refused ({string.Join(", ", target.ErrorCodes)}).")
                    .ConfigureAwait(false);
                return MigratorHost.MigrationFailed;
            default:
                await report.WriteLineAsync("Bootstrap failed: the administrator was not seeded.").ConfigureAwait(false);
                return MigratorHost.MigrationFailed;
        }
    }

    // The operator's subscription on the operator plan (by code: a plan made by hand keeps its id), not yet linked.
    private static async Task EnsureSubscriptionAsync(
        CatalogDbContext catalog, Guid subscriptionId, string customerName, CancellationToken cancellationToken)
    {
        var planCode = PlanComposition.OperatorPlanCode;
        var planId = await catalog.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM catalog.subscription_plans WHERE code = {planCode}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        var now = DateTime.UtcNow;
        var today = DateOnly.FromDateTime(now);
        var active = ActiveStatus;
        await catalog.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO catalog.system_subscriptions (id, customer_name, tenant_name, plan_id, start_date, end_date, is_active, status, contact_email, contact_phone, activated_at, suspended_at, created_at, updated_at)
                 VALUES ({subscriptionId}, {customerName}, {string.Empty}, {planId}, {today}, NULL, TRUE, {active}, NULL, NULL, {now}, NULL, {now}, NULL)
                 ON CONFLICT DO NOTHING
                 """,
                cancellationToken)
            .ConfigureAwait(false);
    }

    // Whether the subscription holds no tenant of another slug and is unlinked or linked to the tenant of this slug: checked
    // under the bootstrap lock before provisioning, so another slug never becomes a second tenant on the operator's
    // subscription, even when an earlier run stopped between provisioning and linking.
    private static async Task<bool> SubscriptionFreeForAsync(
        CatalogDbContext catalog, Guid subscriptionId, string slug, CancellationToken cancellationToken)
    {
        var others = await catalog.Database
            .SqlQuery<int>($"SELECT count(*)::int AS \"Value\" FROM catalog.tenants WHERE subscription_id = {subscriptionId} AND identifier <> {slug}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (others > 0)
        {
            return false;
        }

        var linked = await catalog.Database
            .SqlQuery<string>($"SELECT tenant_name AS \"Value\" FROM catalog.system_subscriptions WHERE id = {subscriptionId}")
            .SingleAsync(cancellationToken).ConfigureAwait(false);
        if (linked.Length == 0)
        {
            return true;
        }

        var tenantId = await TenantIdAsync(catalog, slug, cancellationToken).ConfigureAwait(false);
        return tenantId is { } id && string.Equals(linked, id.ToString("D", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    // The id of the tenant with this slug, or null.
    private static async Task<Guid?> TenantIdAsync(CatalogDbContext catalog, string slug, CancellationToken cancellationToken)
    {
        var ids = await catalog.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM catalog.tenants WHERE identifier = {slug}")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return ids.Count == 0 ? null : ids[0];
    }

    // Links the subscription to the tenant, conditioned on it being unlinked or already this tenant's: false otherwise.
    private static async Task<bool> LinkAsync(CatalogDbContext catalog, Guid subscriptionId, Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        var updated = await catalog.Database.ExecuteSqlAsync(
                $"""
                 UPDATE catalog.system_subscriptions
                 SET tenant_name = {tenant}, updated_at = {now}
                 WHERE id = {subscriptionId} AND tenant_name IN ({string.Empty}, {tenant})
                 """,
                cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }
}
