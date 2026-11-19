using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Pinotechrd.Security.Core;

namespace MyApp.Modules.Access.Infrastructure.Seeding;

/// <summary>
/// Keeps a tenant's security modules and permissions in step with the <see cref="PermissionCatalogue"/>, run by the tenant
/// migrator after the Access migrations, under the tenant's lock and as the migrator's login:
/// <list type="bullet">
/// <item>one <c>security_modules</c> row per module of the catalogue, its id <c>SecurityModuleId.FromCode</c> and its code
/// upper case (<c>ACCESS</c>);</item>
/// <item>one <c>security_permissions</c> row per permission, its id <see cref="SeedIds.Permission"/> and its code upper
/// case (<c>ACCESS.USERS.READ</c>), under the row of its module;</item>
/// <item>when the tenant has the system role <c>Administrator</c> (<see cref="SeedIds.AdministratorRole"/>), every
/// permission granted to it, so a newly declared permission reaches it (one removed by hand comes back).</item>
/// </list>
/// It only adds, in one transaction: rows made by hand are matched by code and kept, and nothing is changed, deactivated or
/// removed. A module or permission row must carry the deterministic id of its code (plans link modules by that id): a row
/// with the code but another id, or with the id but another code, fails the tenant with a message naming the code, and the
/// transaction is rolled back. Every statement is an insert that does nothing on a conflict, with the values as parameters; the tables are
/// found through the tenant's <c>search_path</c>.
/// </summary>
/// <param name="catalogue">The permissions the application declares.</param>
public sealed class TenantSecuritySeeder(PermissionCatalogue catalogue) : ITenantSeeder
{
    /// <inheritdoc />
    public async Task SeedAsync(IServiceProvider scopedServices, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);

        var context = scopedServices.GetRequiredService<AccessDbContext>();
        var tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var moduleIds = await SeedModulesAsync(context, tenant, now, cancellationToken).ConfigureAwait(false);
            var permissionIds = await SeedPermissionsAsync(context, moduleIds, tenant, now, cancellationToken).ConfigureAwait(false);
            await GrantAdministratorAsync(context, permissionIds, tenant, now, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // The module rows, then the id of each by code: each must be SecurityModuleId.FromCode(code), the id plans link.
    private async Task<Dictionary<string, Guid>> SeedModulesAsync(
        AccessDbContext context, string tenant, DateTime now, CancellationToken cancellationToken)
    {
        var modules = PlanComposition.ModulesOf(catalogue);
        for (var order = 0; order < modules.Count; order++)
        {
            var name = modules[order];
            var code = name.ToUpperInvariant();
            await context.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO security_modules (id, name, code, description, is_active, display_order, created_at, updated_at, tenant_id)
                     VALUES ({SecurityModuleId.FromCode(code)}, {name}, {code}, NULL, TRUE, {order}, {now}, NULL, {tenant})
                     ON CONFLICT DO NOTHING
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var codes = modules.Select(m => m.ToUpperInvariant()).ToList();
        var ids = codes.Select(SecurityModuleId.FromCode).ToList();
        var rows = await context.SecurityModules.Where(m => codes.Contains(m.Code) || ids.Contains(m.Id))
            .Select(m => new SeededRow(m.Id, m.Code))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new Dictionary<string, Guid>(StringComparer.Ordinal);
        foreach (var code in codes)
        {
            result[code] = EnsureDeterministic("security module", code, SecurityModuleId.FromCode(code), "SecurityModuleId.FromCode", rows);
        }

        return result;
    }

    // The permission rows, then the id of each by code: each must be SeedIds.Permission(code); a row made by hand keeps its module.
    private async Task<List<Guid>> SeedPermissionsAsync(
        AccessDbContext context, Dictionary<string, Guid> moduleIds, string tenant, DateTime now, CancellationToken cancellationToken)
    {
        var codes = new List<string>(catalogue.Permissions.Count);
        foreach (var permission in catalogue.Permissions)
        {
            var code = permission.ToUpperInvariant();
            var module = permission[..permission.IndexOf('.', StringComparison.Ordinal)];
            var moduleId = moduleIds[module.ToUpperInvariant()];
            codes.Add(code);
            await context.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO security_permissions (id, module_id, name, code, description, "group", is_active, created_at, updated_at, tenant_id)
                     VALUES ({SeedIds.Permission(code)}, {moduleId}, {permission}, {code}, NULL, {module}, TRUE, {now}, NULL, {tenant})
                     ON CONFLICT DO NOTHING
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var ids = codes.Select(SeedIds.Permission).ToList();
        var rows = await context.SecurityPermissions.Where(p => codes.Contains(p.Code) || ids.Contains(p.Id))
            .Select(p => new SeededRow(p.Id, p.Code))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return codes.Select(code => EnsureDeterministic("security permission", code, SeedIds.Permission(code), "SeedIds.Permission", rows))
            .ToList();
    }

    // A seeded row must carry the deterministic id of its code; anything else fails the tenant (the transaction rolls back).
    private static Guid EnsureDeterministic(string kind, string code, Guid expectedId, string idSource, List<SeededRow> rows)
    {
        if (rows.Any(r => string.Equals(r.Code, code, StringComparison.Ordinal) && r.Id != expectedId))
        {
            throw new InvalidOperationException(
                $"The {kind} {code} has an id that is not the deterministic one ({idSource}); give the row that id or remove it, then run the migrator again.");
        }

        var byId = rows.FirstOrDefault(r => r.Id == expectedId);
        if (byId is null)
        {
            throw new InvalidOperationException($"The {kind} {code} was not found after seeding; run the migrator again.");
        }

        if (!string.Equals(byId.Code, code, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"The {kind} with the deterministic id of {code} ({idSource}) has the code {byId.Code} (code mismatch); restore its code or remove the row, then run the migrator again.");
        }

        return expectedId;
    }

    private sealed record SeededRow(Guid Id, string Code);

    // Only the role with the system id: a role named Administrator by a tenant's user gains nothing.
    private static async Task GrantAdministratorAsync(
        AccessDbContext context, List<Guid> permissionIds, string tenant, DateTime now, CancellationToken cancellationToken)
    {
        var roleId = SeedIds.AdministratorRole;
        if (!await context.Roles.AnyAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        foreach (var permissionId in permissionIds)
        {
            await context.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO role_permissions (id, role_id, permission_id, created_at, updated_at, tenant_id)
                     VALUES ({SeedIds.RolePermission(roleId, permissionId)}, {roleId}, {permissionId}, {now}, NULL, {tenant})
                     ON CONFLICT DO NOTHING
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
