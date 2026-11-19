using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Pinotechrd.Security.Enums;

namespace MyApp.Api.Tests.Access;

// Inactive users and inactive permissions grant nothing, whatever the way the permission reached the user (role or per-user
// allow), and filtering inactive rows never turns a per-user deny into an allow. Real PostgreSQL, real host.
[Collection(PostgresContainer.Name)]
public class AccessInactiveStateTests(PostgresFixture postgres)
{
    private static Task SetUserActiveAsync(AccessWorld world, Guid tenant, Guid user, bool active) =>
        AccessDatabase.InTenantAsync(world.Services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var entity = await db.Users.SingleAsync(u => u.Id == user, TestContext.Current.CancellationToken);
            entity.IsActive = active;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    private static Task DeactivateReadPermissionAsync(AccessWorld world, Guid tenant) =>
        AccessDatabase.InTenantAsync(world.Services, tenant, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var permission = await db.SecurityPermissions.SingleAsync(p => p.Code == AccessDatabase.ReadCode, TestContext.Current.CancellationToken);
            permission.IsActive = false;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

    // Breaks when the per-user allow path does not check that the user is active. The control (200 while active) proves the
    // allow is what was granting the access.
    [Fact]
    public async Task AnInactiveUserWithAPerUserAllowIsAnswered403AndTheActiveOneIs200()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.OverrideReadAsync(world.Services, world.TenantA, user, PermissionAccessType.Allow);

        using var active = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));
        await SetUserActiveAsync(world, world.TenantA, user, false);
        using var inactive = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        await ProblemAssert.IsProblemAsync(inactive, 403, "forbidden");
    }

    // Breaks when the role path does not check that the user is active.
    [Fact]
    public async Task AnInactiveUserWhoseRoleGrantsThePermissionIsAnswered403()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);
        await SetUserActiveAsync(world, world.TenantA, user, false);

        using var response = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }

    // Breaks when an inactive permission still counts through a per-user allow (only the role path filters on is_active).
    [Fact]
    public async Task AnInactivePermissionGrantedByAPerUserAllowGrantsNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.OverrideReadAsync(world.Services, world.TenantA, user, PermissionAccessType.Allow);

        using var before = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));
        await DeactivateReadPermissionAsync(world, world.TenantA);
        using var after = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        await ProblemAssert.IsProblemAsync(after, 403, "forbidden");
    }

    // Breaks when dropping the inactive permission from the allowed set also drops the deny in a way that lets the role
    // grant through (a deny must never become an allow).
    [Fact]
    public async Task APerUserDenyOfAnInactivePermissionNeverTurnsIntoAnAllow()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);
        await AccessDatabase.OverrideReadAsync(world.Services, world.TenantA, user, PermissionAccessType.Deny);
        await DeactivateReadPermissionAsync(world, world.TenantA);

        using var response = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }
}
