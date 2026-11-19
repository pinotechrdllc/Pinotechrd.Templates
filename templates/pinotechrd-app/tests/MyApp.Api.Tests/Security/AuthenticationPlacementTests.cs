using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// Authentication and authorization are added by the API pipeline, after routing and the rate limiter and
// inside the error handling. The protected fixture routes exist in every host; hosts that need a user register the test
// scheme through WithServices, and the 403 case a permission checker that grants another permission.
public class AuthenticationPlacementTests
{
    private const string Listed = "https://app.example.com";

    private static ApiHostFactory Authenticated() => new ApiHostFactory().WithServices(TestAuthenticationHandler.Register);

    // Breaks when the application does not answer 401 as an unauthorized problem for an anonymous request, or the
    // anonymous version endpoint stops answering 200.
    [Fact]
    public async Task AnonymousRequestToAProtectedEndpointAnswers401AsAProblem()
    {
        using var factory = Authenticated();
        using var client = factory.CreateClient();

        using var secured = await client.SendAsync(ProblemFixtureEndpoint.Secured);
        using var anonymous = await client.SendAsync(SecurityRequests.Version);

        await ProblemAssert.IsProblemAsync(secured, 401, "unauthorized");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
    }

    // Breaks when an authenticated user without the required permission is not answered 403 forbidden as a problem.
    [Fact]
    public async Task AuthenticatedRequestWithoutTheRequiredPermissionAnswers403AsAProblem()
    {
        using var factory = Authenticated().WithPermissionChecker(new TestPermissionChecker("Fixture.Things.Other"));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ProblemFixtureEndpoint.AdminOnly, user: "alice");

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }

    // Breaks when an authenticated user that satisfies the requirement is rejected, or the identity is not read at all.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.Secured, false)]
    [InlineData(ProblemFixtureEndpoint.Secured, true)]
    [InlineData(ProblemFixtureEndpoint.AdminOnly, true)]
    public async Task AuthenticatedRequestThatSatisfiesTheRequirementAnswers200(string path, bool grantsThePermission)
    {
        using var factory = Authenticated()
            .WithPermissionChecker(new TestPermissionChecker(grantsThePermission ? [ProblemFixtureEndpoint.AdminPermission] : []));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, user: "alice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the 401 or the 403 is written outside the security headers, the no-store policy, CORS or Vary: Origin.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.Secured, false, 401)]
    [InlineData(ProblemFixtureEndpoint.AdminOnly, true, 403)]
    public async Task UnauthorizedAndForbiddenResponsesKeepSecurityCacheAndCorsHeaders(string path, bool authenticated, int status)
    {
        using var factory = Authenticated().WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: Listed, user: authenticated ? "alice" : null);

        Assert.Equal(status, (int)response.StatusCode);
        SecurityAssert.HasStandardHeaders(response);
        Assert.Contains("no-store", response.Value("Cache-Control"), StringComparison.Ordinal);
        SecurityAssert.AllowsOrigin(response, Listed);
        Assert.Equal(1, SecurityAssert.VaryOriginCount(response));
    }

    // Breaks when the limiter runs after authentication: anonymous requests would be answered 401 for ever.
    [Fact]
    public async Task LimiterRunsBeforeAuthentication()
    {
        using var factory = Authenticated().WithLowLimit();
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(ProblemFixtureEndpoint.Secured, "10.6.0.1");
        using var second = await client.SendAsync(ProblemFixtureEndpoint.Secured, "10.6.0.1");
        using var third = await client.SendAsync(ProblemFixtureEndpoint.Secured, "10.6.0.1");

        await ProblemAssert.IsProblemAsync(first, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(second, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(third, 429, "rate_limited");
    }

    // Breaks when a host that registers no authentication scheme of its own no longer starts, or no longer serves its
    // anonymous endpoints, answers 404 for an unknown route, or closes the protected ones: the placeholder scheme
    // authenticates nobody, so a protected endpoint answers 401 (not 500) and the permission one too.
    [Fact]
    public async Task HostWithoutARealAuthenticationSchemeServesAnonymousEndpointsAndDeniesTheRest()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var version = await client.SendAsync(SecurityRequests.Version);
        using var unknown = await client.SendAsync(SecurityRequests.Unknown);
        using var secured = await client.SendAsync(ProblemFixtureEndpoint.Secured);
        using var admin = await client.SendAsync(ProblemFixtureEndpoint.AdminOnly);

        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
        await ProblemAssert.IsProblemAsync(unknown, 404, "not_found");
        await ProblemAssert.IsProblemAsync(secured, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(admin, 401, "unauthorized");
    }
}
