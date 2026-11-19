using System.Net;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using Pinotechrd.Security.Enums;
using Pinotechrd.Security.Interfaces;
using MyApp.Modules.Access.Infrastructure.Persistence;

namespace MyApp.Api.Tests.Access;

// The permission of an endpoint is decided by the Access module on the tenant's own tables: a user's roles, plus the per-user
// allows, minus the per-user denies, inside the tenant of the token and nowhere else. Real PostgreSQL (Docker), real host,
// bearer tokens signed with the fake key of AccessJwt.
[Collection(PostgresContainer.Name)]
public class AccessAuthorizationTests(PostgresFixture postgres)
{
    // Breaks when a user whose role grants the permission is refused (the checker does not reach the tenant tables, the
    // permission code is compared in another form, or the token's claims are not read).
    [Fact]
    public async Task AUserWhoseRoleGrantsThePermissionIsAnswered200()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);

        using var response = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the permission is granted to a user that has no role with it (an open default, or a checker that only
    // looks at the module).
    [Fact]
    public async Task AUserWithoutARoleThatGrantsThePermissionIsAnswered403AsAProblem()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);

        using var response = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
        Assert.Empty(response.Headers.WwwAuthenticate);
        Assert.Null(response.Headers.Location);
    }

    // Breaks when the per-user deny stops winning over the role: the deny is applied last.
    [Fact]
    public async Task APerUserDenyRemovesAPermissionTheRoleGrants()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);
        await AccessDatabase.OverrideReadAsync(world.Services, world.TenantA, user, PermissionAccessType.Deny);

        using var response = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }

    // Breaks when the per-user allow is ignored (only roles are read).
    [Fact]
    public async Task APerUserAllowGrantsAPermissionWithoutAnyRole()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.OverrideReadAsync(world.Services, world.TenantA, user, PermissionAccessType.Allow);

        using var response = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the permission of one user is given to another one of the same tenant.
    [Fact]
    public async Task APermissionGrantedToOneUserIsNotGrantedToAnotherUserOfTheSameTenant()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var granted = await world.AddUserAsync(world.TenantA);
        var other = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, granted);

        using var forGranted = await world.GetReadAsync(AccessJwt.TokenFor(granted, world.TenantA));
        using var forOther = await world.GetReadAsync(AccessJwt.TokenFor(other, world.TenantA));

        Assert.Equal(HttpStatusCode.OK, forGranted.StatusCode);
        await ProblemAssert.IsProblemAsync(forOther, 403, "forbidden");
    }

    // Breaks when the tenant of the token is not the tenant whose tables are read: the same user id with a token for tenant B
    // would then keep the roles it has in tenant A. The control (200 for tenant A) proves the role is there to be found.
    [Fact]
    public async Task ATokenForAnotherTenantNeverUsesTheRolesOfTheUsersOwnTenant()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);

        using var own = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));
        using var other = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantB));

        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        await ProblemAssert.IsProblemAsync(other, 403, "forbidden");
    }

    // Breaks when a per-user allow of tenant A counts for a token of tenant B.
    [Fact]
    public async Task ATokenForAnotherTenantNeverUsesThePerUserAllowOfTheUsersOwnTenant()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.OverrideReadAsync(world.Services, world.TenantA, user, PermissionAccessType.Allow);

        using var other = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantB));

        await ProblemAssert.IsProblemAsync(other, 403, "forbidden");
    }

    // Breaks when the tenant of the request is chosen by anything but the token (here a header and a query string naming A
    // while the token says B).
    [Fact]
    public async Task TheTenantComesFromTheTokenAndNotFromAHeaderOrTheQueryString()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{AccessHost.ReadRoute}?tenant_id={world.TenantA:D}&tenantId={world.TenantA:D}");
        request.Headers.TryAddWithoutValidation("X-Tenant-Id", world.TenantA.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Tenant", world.TenantA.ToString("D"));
        request.Headers.Authorization = new("Bearer", AccessJwt.TokenFor(user, world.TenantB));

        using var response = await world.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }

    // Breaks when the claims the library writes and the claims the API reads are not the same ones: a token issued by the
    // library's own login must give ICurrentUser the user and pass the permission check, with no hand made claim.
    [Fact]
    public async Task ATokenIssuedByTheLibrariesLoginIdentifiesTheUserAndPassesThePermissionCheck()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var email = $"login-{Guid.NewGuid():N}@example.test";
        var user = await world.AddUserAsync(world.TenantA, email);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);
        string token = null!;
        await AccessDatabase.InTenantAsync(world.Services, world.TenantA, async scope =>
        {
            var login = await scope.GetRequiredService<IPinotechrdAuthManager<AccessUser>>()
                .LoginAsync(email, AccessDatabase.DefaultPassword, world.TenantA.ToString("D"));
            Assert.True(login.Succeeded, login.ErrorMessage);
            token = login.Token!.AccessToken;
        });

        using var whoAmI = await world.Client.GetWithBearerAsync(AccessHost.WhoAmIRoute, token);
        using var read = await world.GetReadAsync(token);

        var seen = await whoAmI.ReadWhoAmIAsync();
        Assert.True(seen.UserAuthenticated);
        Assert.Equal(user, seen.UserId);
        Assert.Equal(world.TenantA, seen.TenantId);
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
    }

    // Breaks when the permission claims of the token are trusted: a token that claims every permission, for a user that has
    // none in the database, must still be refused. The database is the only source.
    [Fact]
    public async Task PermissionClaimsInTheTokenGrantNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        var token = AccessJwt.Token(
        [
            new Claim("sub", user.ToString("D")),
            new Claim("tenant_id", world.TenantA.ToString("D")),
            new Claim("permission", AccessDatabase.ReadCode),
            new Claim("permission", AccessHost.ReadPermission),
        ]);

        using var response = await world.GetReadAsync(token);

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }

    // Breaks when a role's permission still grants the access after its module was switched off: the grant must be an
    // active permission of an active module.
    [Fact]
    public async Task APermissionOfAnInactiveModuleGrantsNothing()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);
        await AccessDatabase.InTenantAsync(world.Services, world.TenantA, async scope =>
        {
            var db = scope.GetRequiredService<AccessDbContext>();
            var module = await db.SecurityModules.SingleAsync(m => m.Id == AccessDatabase.AccessModuleId, TestContext.Current.CancellationToken);
            module.IsActive = false;
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        });

        using var response = await world.GetReadAsync(AccessJwt.TokenFor(user, world.TenantA));

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }

    // Breaks when a token whose user does not exist in the tenant is a server error or a challenge: the checker answers no,
    // so the answer is the same 403 as for a user without the permission.
    [Fact]
    public async Task ATokenForAUserThatDoesNotExistInTheTenantIsAnswered403()
    {
        using var world = await AccessWorld.StartAsync(postgres);

        using var response = await world.GetReadAsync(AccessJwt.TokenFor(Guid.NewGuid(), world.TenantA));

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
        Assert.Empty(response.Headers.WwwAuthenticate);
    }
}
