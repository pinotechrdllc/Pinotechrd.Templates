using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// With a scheme that authenticates a user (the test scheme), the three kinds of endpoint answer as documented: an
// authenticated-only endpoint 200, a permission endpoint what the permission checker says, and the checker is asked only
// for permission endpoints, with the exact permission and the current user.
public class PermissionAuthorizationTests
{
    private const string OtherPermission = "Fixture.Things.Other";

    private const string OtherRoute = "/api/v1/permission-fixtures/other";

    private static ApiHostFactory Authenticated() =>
        new ApiHostFactory()
            .WithServices(TestAuthenticationHandler.Register)
            .WithServices(services => services.AddPermissions(OtherPermission))
            .WithEndpoints(routes => routes.MapGet("/permission-fixtures/other", () => Results.Ok()).RequirePermission(OtherPermission));

    // Breaks when an authenticated user is turned away from an endpoint that asks no permission.
    [Fact]
    public async Task AnAuthenticatedOnlyEndpointAnswers200ToAnAuthenticatedUser()
    {
        using var factory = Authenticated();
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(ProblemFixtureEndpoint.Secured, "alice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the default checker grants anything: until a module registers one, every permission is denied.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.AdminOnly)]
    [InlineData(OtherRoute)]
    public async Task APermissionEndpointAnswers403ForbiddenWithTheDefaultChecker(string path)
    {
        using var factory = Authenticated();
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(path, "alice");

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
        Assert.Empty(response.Values("WWW-Authenticate"));
    }

    // Breaks when a granted permission is not honoured, or when granting one permission opens another endpoint.
    [Fact]
    public async Task ACheckerThatGrantsAPermissionOpensItsEndpointAndOnlyThat()
    {
        var checker = new TestPermissionChecker(ProblemFixtureEndpoint.AdminPermission);
        using var factory = Authenticated().WithPermissionChecker(checker);
        using var client = factory.CreateClient();

        using var granted = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice");
        using var notGranted = await client.GetAsAsync(OtherRoute, "alice");

        Assert.Equal(HttpStatusCode.OK, granted.StatusCode);
        await ProblemAssert.IsProblemAsync(notGranted, 403, "forbidden");
    }

    // Breaks when the checker is given another string than the one the endpoint declared (a normalised or a prefixed name),
    // or another user than the one of the request, or is asked while the user is not yet set.
    [Fact]
    public async Task TheCheckerReceivesTheExactPermissionAndTheCurrentUsersId()
    {
        var checker = new TestPermissionChecker(ProblemFixtureEndpoint.AdminPermission);
        var userId = Guid.Parse("5b1e8f10-3a4c-4d2e-9c7b-0a1b2c3d4e5f");
        using var factory = Authenticated().WithPermissionChecker(checker);
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice", IdentityRequests.ClaimsFor(userId, IdentityRequests.TenantA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(checker.Calls);
        Assert.Equal(ProblemFixtureEndpoint.AdminPermission, call.Permission);
        Assert.True(call.IsAuthenticated);
        Assert.Equal(userId, call.UserId);
    }

    // Breaks when the checker is asked for requests that cannot be granted anything or that ask no permission: for an
    // anonymous caller (401 first), for an authenticated-only endpoint, for an anonymous endpoint, or for an unknown route.
    [Fact]
    public async Task TheCheckerIsNotAskedForAnAnonymousRequestOrForAnEndpointThatNeedsNoPermission()
    {
        var checker = new TestPermissionChecker(ProblemFixtureEndpoint.AdminPermission, OtherPermission);
        using var factory = Authenticated().WithPermissionChecker(checker);
        using var client = factory.CreateClient();

        using var anonymousOnPermission = await client.SendAsync(ProblemFixtureEndpoint.AdminOnly);
        using var anonymousOnOther = await client.SendAsync(OtherRoute);
        using var authenticatedOnly = await client.GetAsAsync(ProblemFixtureEndpoint.Secured, "alice");
        using var anonymousEndpoint = await client.GetAsAsync(SecurityRequests.Version, "alice");
        using var unknown = await client.GetAsAsync(SecurityRequests.Unknown, "alice");

        await ProblemAssert.IsProblemAsync(anonymousOnPermission, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(anonymousOnOther, 401, "unauthorized");
        Assert.Equal(HttpStatusCode.OK, authenticatedOnly.StatusCode);
        Assert.Equal(HttpStatusCode.OK, anonymousEndpoint.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Empty(checker.Calls);
    }

    // Breaks when a permission endpoint asks the checker more than once, or for another permission than its own, in a
    // sequence of requests of different users.
    [Fact]
    public async Task EachPermissionRequestAsksTheCheckerOnceForItsOwnUser()
    {
        var checker = new TestPermissionChecker(ProblemFixtureEndpoint.AdminPermission);
        using var factory = Authenticated().WithPermissionChecker(checker);
        using var client = factory.CreateClient();

        using var alice = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice");
        using var bob = await client.GetAsAsync(OtherRoute, "bob");

        Assert.Equal(
            [
                (ProblemFixtureEndpoint.AdminPermission, TestAuthenticationHandler.UserIdOf("alice")),
                (OtherPermission, TestAuthenticationHandler.UserIdOf("bob")),
            ],
            checker.Calls.Select(c => (c.Permission, c.UserId!.Value)));
    }

    // Breaks when a checker registered the documented way (AddPermissionChecker, a scoped keyed service) is ignored.
    [Fact]
    public async Task AScopedCheckerRegisteredForTheModuleReplacesTheDefaultDenial()
    {
        using var factory = Authenticated().WithServices(services => services.AddPermissionChecker<GrantEverythingChecker>("Fixture"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when a checker that fails is answered as anything but the plain 500 internal_error, or leaks its message.
    [Fact]
    public async Task ACheckerThatThrowsAnswers500InternalErrorWithoutItsMessage()
    {
        using var factory = Authenticated().WithServices(services => services.AddPermissionChecker<ThrowingChecker>("Fixture"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice");

        await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        Assert.DoesNotContain(ProblemFixtureEndpoint.Secret, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    // Breaks when the 401 or the 403 of a permission endpoint is written without a body the client can read as a problem.
    [Fact]
    public async Task TheForbiddenAnswerIsAProblemDocumentWithATraceId()
    {
        using var factory = Authenticated();
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice");

        var problem = await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
        Assert.Equal(403, problem.GetProperty("status").GetInt32());
    }

    // A user is a scoped service: outside a request (a job) there is none, and asking for the id is an error.
    [Fact]
    public void OutsideARequestThereIsNoCurrentUserAndItsIdIsRefused()
    {
        using var factory = Authenticated();
        using var scope = factory.Services.CreateScope();

        var user = scope.ServiceProvider.GetRequiredService<ICurrentUser>();

        Assert.False(user.IsAuthenticated);
        Assert.Throws<InvalidOperationException>(() => user.UserId);
    }

    private sealed class GrantEverythingChecker : IPermissionChecker
    {
        public ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class ThrowingChecker : IPermissionChecker
    {
        public ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken) =>
            throw new InvalidOperationException(ProblemFixtureEndpoint.Secret);
    }
}
