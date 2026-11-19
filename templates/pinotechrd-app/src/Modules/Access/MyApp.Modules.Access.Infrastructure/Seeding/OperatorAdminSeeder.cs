using System.Globalization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;

namespace MyApp.Modules.Access.Infrastructure.Seeding;

/// <summary>
/// The first administrator of the operator tenant, registered only for the migrator's <c>bootstrap</c> command and run by
/// the tenant migrator after <see cref="TenantSecuritySeeder"/>, under the tenant's lock (so concurrent runs take turns),
/// for the tenant of <see cref="BootstrapTarget"/> only. In one transaction:
/// <list type="bullet">
/// <item>the system role <see cref="AdministratorRoleName"/> (id <see cref="SeedIds.AdministratorRole"/>) and every
/// permission of the catalogue granted to it, inserted when missing;</item>
/// <item>the user with the target's email, created through Identity (<c>UserName</c> the email, email confirmed, the
/// password hashed by the Access password policy) when no user has that email; an existing user is never changed, its
/// password never reset;</item>
/// <item>that user a member of the role.</item>
/// </list>
/// The tenant has one first administrator: when the role already has members and none has the target's email, nothing is
/// written (a rerun with another email never adds a second administrator); the same email stays idempotent. When the
/// user is missing and no password was given, or Identity refuses the user, nothing is written either. The outcome is
/// recorded in the target. No message names the email or the password; Identity's errors are kept by code only.
/// </summary>
/// <param name="target">The tenant and administrator the command targets.</param>
/// <param name="catalogue">The permissions the application declares.</param>
public sealed class OperatorAdminSeeder(BootstrapTarget target, PermissionCatalogue catalogue) : ITenantSeeder
{
    /// <summary>The name of the system role of the administrators.</summary>
    public const string AdministratorRoleName = "Administrator";

    /// <inheritdoc />
    public async Task SeedAsync(IServiceProvider scopedServices, Guid tenantId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);

        if (!target.TryGetAdmin(tenantId, out var email, out var password))
        {
            return;
        }

        var users = scopedServices.GetRequiredService<UserManager<AccessUser>>();
        var context = scopedServices.GetRequiredService<AccessDbContext>();
        if (await HasAnotherAdministratorAsync(context, users.NormalizeEmail(email), cancellationToken).ConfigureAwait(false))
        {
            target.Complete(BootstrapAdminOutcome.AnotherAdministrator);
            return;
        }

        var existing = await users.FindByEmailAsync(email).ConfigureAwait(false);
        if (existing is null && password is null)
        {
            target.Complete(BootstrapAdminOutcome.PasswordRequired);
            return;
        }

        var tenant = tenantId.ToString("D", CultureInfo.InvariantCulture);
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var roleId = await EnsureRoleAsync(context, cancellationToken).ConfigureAwait(false);
            await GrantAllAsync(context, roleId, tenant, cancellationToken).ConfigureAwait(false);

            var userId = existing?.Id;
            if (userId is null)
            {
                var user = new AccessUser
                {
                    Id = Guid.CreateVersion7(),
                    UserName = email,
                    Email = email,
                    EmailConfirmed = true,
                    IsActive = true,
                };
                var created = await users.CreateAsync(user, password!).ConfigureAwait(false);
                if (!created.Succeeded)
                {
                    // Rolled back when the transaction is disposed uncommitted: the role is not left behind either.
                    target.Complete(BootstrapAdminOutcome.Failed, [.. created.Errors.Select(e => e.Code).Distinct(StringComparer.Ordinal)]);
                    return;
                }

                userId = user.Id;
            }

            await context.Database.ExecuteSqlAsync(
                    $"""
                     INSERT INTO user_roles (user_id, role_id)
                     VALUES ({userId.Value}, {roleId})
                     ON CONFLICT DO NOTHING
                     """,
                    cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        target.Complete(existing is null ? BootstrapAdminOutcome.Created : BootstrapAdminOutcome.AlreadyPresent);
    }

    // Whether the system role has members and none of them has this normalized email: seeding would add another
    // administrator. When this email is already one of them (even among administrators added later), nothing is added.
    private static async Task<bool> HasAnotherAdministratorAsync(AccessDbContext context, string? normalizedEmail, CancellationToken cancellationToken)
    {
        var roleId = SeedIds.AdministratorRole;
        var emails = from membership in context.UserRoles
                     join user in context.Users on membership.UserId equals user.Id
                     where membership.RoleId == roleId
                     select user.NormalizedEmail;
        return await emails.AnyAsync(cancellationToken).ConfigureAwait(false)
               && !await emails.AnyAsync(e => e == normalizedEmail, cancellationToken).ConfigureAwait(false);
    }

    // The system role, by its fixed id; a role of that name with another id (made by hand) is not adopted.
    private static async Task<Guid> EnsureRoleAsync(AccessDbContext context, CancellationToken cancellationToken)
    {
        var roleId = SeedIds.AdministratorRole;
        var normalized = AdministratorRoleName.ToUpperInvariant();
        var stamp = roleId.ToString("D", CultureInfo.InvariantCulture);
        var now = DateTime.UtcNow;
        await context.Database.ExecuteSqlAsync(
                $"""
                 INSERT INTO roles (id, name, normalized_name, concurrency_stamp, description, created_at)
                 VALUES ({roleId}, {AdministratorRoleName}, {normalized}, {stamp}, {"Every permission of the tenant."}, {now})
                 ON CONFLICT DO NOTHING
                 """,
                cancellationToken)
            .ConfigureAwait(false);

        if (!await context.Roles.AnyAsync(r => r.Id == roleId, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"A role named {AdministratorRoleName} exists without the system id (SeedIds.AdministratorRole); rename or remove it, then run the bootstrap again.");
        }

        return roleId;
    }

    // Every permission of the catalogue: TenantSecuritySeeder, which ran first, gave each the id SeedIds.Permission(code).
    private async Task GrantAllAsync(AccessDbContext context, Guid roleId, string tenant, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        foreach (var permission in catalogue.Permissions)
        {
            var permissionId = SeedIds.Permission(permission);
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
