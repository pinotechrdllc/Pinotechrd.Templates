using Microsoft.AspNetCore.Identity;
using Pinotechrd.Security.Entities;
using Pinotechrd.Security.Enums;
using Pinotechrd.Security.Tenancy;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

/// <summary>
/// Catalog: plan BASIC with module SALES only; active subscriptions and tenants "a" and "b".
/// Each tenant: modules SALES and REPORTS, permissions SALES.VIEW, SALES.EDIT, REPORTS.VIEW, a role "clerk" granted
/// all three, user "pinotechrd.user" in the role. Tenant a only: a per-user deny of SALES.EDIT and a user "only-a".
/// </summary>
public static class Seed
{
    public const string UserName = "pinotechrd.user";
    public const string LockoutUserName = "pinotechrd.lockout";
    public const string OnlyAUserName = "pinotechrd.only-a";
    public const string Password = "Pinotechrd-Spike-1"; // test value for throwaway containers

    // The catalog maps plans to modules by module id, so a module must have the same id in every tenant schema.
    public static readonly Guid SalesModuleId = Guid.Parse("5a1e5000-0000-4000-8000-000000000001");
    public static readonly Guid ReportsModuleId = Guid.Parse("5a1e5000-0000-4000-8000-000000000002");

    public static async Task<Dictionary<string, Guid>> RunAsync(IServiceProvider services)
    {
        await using (var scope = services.CreateAsyncScope())
        {
            var catalog = scope.ServiceProvider.GetRequiredService<PinotechrdCatalogDbContext>();
            var plan = SubscriptionPlan.Create("Basic", "BASIC", null, null);
            catalog.SubscriptionPlans.Add(plan);
            catalog.SubscriptionPlanModules.Add(SubscriptionPlanModule.Create(plan.Id, SalesModuleId));
            foreach (var id in new[] { "a", "b" })
            {
                var sub = SystemSubscription.Create($"Pinotechrd Spike {id}", id, plan.Id, DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-1));
                catalog.SystemSubscriptions.Add(sub);
                var tenant = Tenant.Create(sub.Id, $"Tenant {id}", id, $"tenant_{id}");
                tenant.Status = TenantStatus.Active;
                catalog.Tenants.Add(tenant);
            }

            await catalog.SaveChangesAsync();
        }

        var users = new Dictionary<string, Guid>();
        foreach (var id in new[] { "a", "b" })
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<TenantContext>().SetTenant(id);
            var db = scope.ServiceProvider.GetRequiredService<PinotechrdTenantSecurityDbContext>();
            var sales = SecurityModule.Create("Sales", "SALES");
            sales.Id = SalesModuleId;
            var reports = SecurityModule.Create("Reports", "REPORTS");
            reports.Id = ReportsModuleId;
            var salesView = SecurityPermission.Create(SalesModuleId, "View", "SALES.VIEW");
            var salesEdit = SecurityPermission.Create(SalesModuleId, "Edit", "SALES.EDIT");
            var reportsView = SecurityPermission.Create(ReportsModuleId, "View", "REPORTS.VIEW");
            db.SecurityModules.AddRange(sales, reports);
            db.SecurityPermissions.AddRange(salesView, salesEdit, reportsView);
            await db.SaveChangesAsync();

            var roles = scope.ServiceProvider.GetRequiredService<RoleManager<AppRole>>();
            var role = new AppRole { Name = "clerk" };
            Ensure(await roles.CreateAsync(role));
            db.RolePermissions.AddRange(
                RolePermission.Create(role.Id, salesView.Id),
                RolePermission.Create(role.Id, salesEdit.Id),
                RolePermission.Create(role.Id, reportsView.Id));
            await db.SaveChangesAsync();

            var userManager = scope.ServiceProvider.GetRequiredService<UserManager<AppUser>>();
            var user = new AppUser { UserName = UserName, Email = $"user@{id}.example.test" };
            Ensure(await userManager.CreateAsync(user, Password));
            Ensure(await userManager.AddToRoleAsync(user, "clerk"));
            Ensure(await userManager.CreateAsync(new AppUser { UserName = LockoutUserName, Email = $"lockout@{id}.example.test" }, Password));
            if (id == "a")
            {
                Ensure(await userManager.CreateAsync(new AppUser { UserName = OnlyAUserName, Email = "only@a.example.test" }, Password));
                db.UserModulePermissions.Add(UserModulePermission.CreateDeny(user.Id, salesEdit.Id));
                await db.SaveChangesAsync();
            }

            users[id] = user.Id;
        }

        return users;
    }

    private static void Ensure(IdentityResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(string.Join("; ", result.Errors.Select(e => e.Description)));
        }
    }
}
