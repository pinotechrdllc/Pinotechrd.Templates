using System.Net;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// Criterion 31: authentication and authorization are added by the API pipeline, after routing and the rate limiter and
// inside the error handling. The secured fixture routes exist in every host (they are never requested in a host
// without authentication), and hosts that need authentication register the test scheme through WithServices.
public class AuthenticationPlacementTests
{
    private const string Listed = "https://app.example.com";

    private static ApiHostFactory Authenticated() => new ApiHostFactory().WithServices(TestAuthenticationHandler.Register);

    // Breaks when the application does not answer 401 as an unauthorized problem for an anonymous request, or the
    // anonymous version endpoint stops answering 200.
    [Fact]
    public async Task AnonymousRequestToASecuredEndpointAnswers401AsAProblem()
    {
        using var factory = Authenticated();
        using var client = factory.CreateClient();

        using var secured = await client.SendAsync(ProblemFixtureEndpoint.Secured);
        using var anonymous = await client.SendAsync(SecurityRequests.Version);

        await ProblemAssert.IsProblemAsync(secured, 401, "unauthorized");
        Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
    }

    // Breaks when an authenticated user without the required role is not answered 403 forbidden as a problem.
    [Fact]
    public async Task AuthenticatedRequestWithoutTheRequiredRoleAnswers403AsAProblem()
    {
        using var factory = Authenticated();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ProblemFixtureEndpoint.AdminOnly, user: "alice", roles: "reader");

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }

    // Breaks when an authenticated user that satisfies the requirement is rejected, or the identity is not read at all.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.Secured, null)]
    [InlineData(ProblemFixtureEndpoint.Secured, "reader")]
    [InlineData(ProblemFixtureEndpoint.AdminOnly, "admin")]
    [InlineData(ProblemFixtureEndpoint.AdminOnly, "reader, admin")]
    public async Task AuthenticatedRequestThatSatisfiesTheRequirementAnswers200(string path, string? roles)
    {
        using var factory = Authenticated();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, user: "alice", roles: roles);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the 401 or the 403 is written outside the security headers, the no-store policy, CORS or Vary: Origin.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.Secured, null, 401)]
    [InlineData(ProblemFixtureEndpoint.AdminOnly, "reader", 403)]
    public async Task UnauthorizedAndForbiddenResponsesKeepSecurityCacheAndCorsHeaders(string path, string? roles, int status)
    {
        using var factory = Authenticated().WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: Listed, user: roles is null ? null : "alice", roles: roles);

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

    // Breaks when a host without authentication services no longer starts or serves, for example because the pipeline
    // requires the services. The secured routes are present in this host and are simply never requested.
    [Fact]
    public async Task HostWithoutAuthenticationServicesStartsAndServesAsBefore()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(SecurityRequests.Version);
        using var unknown = await client.SendAsync(SecurityRequests.Unknown);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await ProblemAssert.IsProblemAsync(unknown, 404, "not_found");
    }
}
