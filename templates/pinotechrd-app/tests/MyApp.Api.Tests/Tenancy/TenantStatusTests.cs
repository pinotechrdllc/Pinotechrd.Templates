using System.Net;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Tenancy;
using Pinotechrd.Security.Enums;

namespace MyApp.Api.Tests.Tenancy;

// A token of a tenant that is not active is refused with 403 tenant_unavailable, whatever the endpoint asks of the user,
// and the status is read on every request so a suspension applies at once and a reactivation without a restart.
[Collection(PostgresContainer.Name)]
public class TenantStatusTests(PostgresFixture postgres)
{
    private static readonly string[] Routes = [AccessHost.AuthenticatedRoute, AccessHost.ReadRoute];

    private async Task<(AccessWorld World, string TokenA, string TokenB)> StartAsync()
    {
        var world = await AccessWorld.StartAsync(postgres);
        var a = await world.AddUserAsync(world.TenantA);
        var b = await world.AddUserAsync(world.TenantB);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, a);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantB, b);
        return (world, AccessJwt.TokenFor(a, world.TenantA), AccessJwt.TokenFor(b, world.TenantB));
    }

    // Breaks when the status is not checked on endpoints that only need a signed-in user, or on endpoints that need a permission.
    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Cancelled)]
    [InlineData(TenantStatus.Provisioning)]
    public async Task ATokenOfATenantThatIsNotActiveIsAnswered403TenantUnavailableOnEveryKindOfEndpoint(TenantStatus status)
    {
        var (world, tokenA, _) = await StartAsync();
        using var owned = world;
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, status);

        foreach (var route in Routes)
        {
            using var response = await world.Client.GetWithBearerAsync(route, tokenA);
            await ProblemAssert.IsProblemAsync(response, 403, "tenant_unavailable");
        }
    }

    // Breaks when the status is cached or the check is skipped for a tenant that was active at some point: 200, 403, 200.
    [Fact]
    public async Task ASuspensionAppliesAtOnceAndAReactivationWithoutARestart()
    {
        var (world, tokenA, _) = await StartAsync();
        using var owned = world;

        foreach (var route in Routes)
        {
            using var before = await world.Client.GetWithBearerAsync(route, tokenA);
            await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Suspended);
            using var suspended = await world.Client.GetWithBearerAsync(route, tokenA);
            await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Active);
            using var after = await world.Client.GetWithBearerAsync(route, tokenA);

            Assert.Equal(HttpStatusCode.OK, before.StatusCode);
            await ProblemAssert.IsProblemAsync(suspended, 403, "tenant_unavailable");
            Assert.Equal(HttpStatusCode.OK, after.StatusCode);
        }
    }

    // Breaks when the status of one tenant is applied to every tenant, or ignored for the right one.
    [Fact]
    public async Task SuspendingOneTenantDoesNotRefuseTheTokensOfAnother()
    {
        var (world, tokenA, tokenB) = await StartAsync();
        using var owned = world;
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Suspended);

        using var forA = await world.Client.GetWithBearerAsync(AccessHost.AuthenticatedRoute, tokenA);
        using var forB = await world.Client.GetWithBearerAsync(AccessHost.AuthenticatedRoute, tokenB);

        Assert.Equal(HttpStatusCode.Forbidden, forA.StatusCode);
        Assert.Equal(HttpStatusCode.OK, forB.StatusCode);
    }

    // Breaks when the status check answers before authentication: a request without a token stays 401, not 403.
    [Fact]
    public async Task ARequestWithoutATokenIsStillAnswered401WhenATenantIsSuspended()
    {
        var (world, _, _) = await StartAsync();
        using var owned = world;
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Suspended);

        using var response = await world.Client.GetWithBearerAsync(AccessHost.AuthenticatedRoute, null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Breaks when the status check is skipped on endpoints that allow anonymous access: a token of a suspended tenant that
    // reaches one must still be refused 403 tenant_unavailable, not served.
    [Theory]
    [InlineData(TenantStatus.Suspended)]
    [InlineData(TenantStatus.Cancelled)]
    public async Task ATokenOfATenantThatIsNotActiveIsAnswered403TenantUnavailableOnAnAnonymousEndpoint(TenantStatus status)
    {
        var (world, tokenA, tokenB) = await StartAsync();
        using var owned = world;
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, status);

        using var refused = await world.Client.GetWithBearerAsync(AccessHost.UnmarkedRoute, tokenA);
        using var served = await world.Client.GetWithBearerAsync(AccessHost.UnmarkedRoute, tokenB);

        await ProblemAssert.IsProblemAsync(refused, 403, "tenant_unavailable");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
    }

    // Breaks when a catalog that fails is read as "active": the request must end in 500, never in the endpoint's normal answer.
    [Theory]
    [InlineData(AccessHost.AuthenticatedRoute)]
    [InlineData(AccessHost.ReadRoute)]
    [InlineData(AccessHost.UnmarkedRoute)]
    public async Task ACatalogThatFailsOnTheStatusCheckEndsAnAuthenticatedRequestIn500(string route)
    {
        using var world = await AccessWorld.StartAsync(
            postgres, factory => factory.WithServices(services => services.AddScoped<ITenantCatalog, ThrowingTenantCatalog>()));
        var user = await world.AddUserAsync(world.TenantA);
        await AccessDatabase.GrantReadThroughARoleAsync(world.Services, world.TenantA, user);

        using var response = await world.Client.GetWithBearerAsync(route, AccessJwt.TokenFor(user, world.TenantA));

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
    }
}
