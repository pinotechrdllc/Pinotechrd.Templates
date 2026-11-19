using Microsoft.EntityFrameworkCore;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;
using Pinotechrd.Security.Core;

namespace MyApp.Modules.Access.Infrastructure.Seeding;

/// <summary>What one run of <see cref="CatalogPlanSeeder"/> added.</summary>
/// <param name="PlansAdded">The plans inserted.</param>
/// <param name="PlanModulesAdded">The plan-module rows inserted.</param>
public sealed record CatalogSeedResult(int PlansAdded, int PlanModulesAdded);

/// <summary>
/// Keeps the catalog's subscription plans in step with the code: each plan of <see cref="PlanComposition.For"/>, matched
/// by its code, and each of its modules, by the module's id (<c>SecurityModuleId.FromCode</c>, the id every tenant gives
/// the module). It only adds: a plan or a plan-module row made by hand is adopted as it is, and nothing is changed,
/// deactivated or removed. Every statement is an insert that does nothing on a conflict, with the values as parameters,
/// so concurrent runs end with one row each. The migrator runs it once the catalog is migrated.
/// </summary>
/// <param name="catalog">The catalog context of the scope.</param>
/// <param name="catalogue">The permissions the application declares.</param>
public sealed class CatalogPlanSeeder(CatalogDbContext catalog, PermissionCatalogue catalogue)
{
    /// <summary>Adds the plans and plan modules the catalog lacks.</summary>
    /// <param name="cancellationToken">Stops the seeding; what was added stays.</param>
    /// <returns>What was added.</returns>
    public async Task<CatalogSeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var plans = 0;
        var planModules = 0;
        foreach (var plan in PlanComposition.For(catalogue))
        {
            var now = DateTime.UtcNow;
            plans += await catalog.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO catalog.subscription_plans (id, name, code, description, is_active, monthly_price, created_at, updated_at)
                     VALUES ({SeedIds.Plan(plan.Code)}, {plan.Name}, {plan.Code}, NULL, TRUE, NULL, {now}, NULL)
                     ON CONFLICT DO NOTHING
                     """,
                    cancellationToken)
                .ConfigureAwait(false);

            // By code: a plan made by hand with that code keeps its own id.
            var code = plan.Code;
            var planId = await catalog.SubscriptionPlans.Where(p => p.Code == code).Select(p => p.Id)
                .SingleAsync(cancellationToken).ConfigureAwait(false);
            foreach (var module in plan.Modules)
            {
                var moduleId = SecurityModuleId.FromCode(module);
                planModules += await catalog.Database.ExecuteSqlAsync(
                        $"""
                         INSERT INTO catalog.subscription_plan_modules (id, plan_id, module_id, created_at, updated_at)
                         SELECT {SeedIds.PlanModule(planId, module)}, {planId}, {moduleId}, {now}, NULL
                         WHERE NOT EXISTS (SELECT 1 FROM catalog.subscription_plan_modules WHERE plan_id = {planId} AND module_id = {moduleId})
                         ON CONFLICT DO NOTHING
                         """,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        return new CatalogSeedResult(plans, planModules);
    }
}
