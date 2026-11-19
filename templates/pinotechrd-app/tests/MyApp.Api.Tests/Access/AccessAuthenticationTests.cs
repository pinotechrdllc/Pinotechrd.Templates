using System.Security.Claims;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Access;

// What the bearer authentication of the Access module accepts and how it refuses the rest: a 401 problem document with the
// Bearer challenge, never a redirect to a login page. Refused before any database access, so no container is needed except
// where a valid token must reach the permission check.
public class AccessAuthenticationTests
{
    private static readonly Guid User = Guid.Parse("5d3c1a52-6f0e-4f43-8d1b-2a7e5c9b0f11");

    private static readonly Guid Tenant = Guid.Parse("6c9e6679-7425-40de-944b-e07fc1f90ae7");

    private static async Task<HttpResponseMessage> GetAsync(string path, string? token)
    {
        using var factory = AccessHost.WithoutDatabase();
        using var client = factory.CreateClient();
        return await client.GetWithBearerAsync(path, token);
    }

    // Breaks when anonymous access to a protected endpoint is answered by a redirect (the Identity cookie challenge), another
    // status, another challenge or a body that is not a problem document.
    [Fact]
    public async Task AnAnonymousCallToAProtectedEndpointAnswers401WithTheBearerChallengeAndNoRedirect()
    {
        using var response = await GetAsync(AccessHost.ReadRoute, token: null);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
        Assert.Equal(["Bearer"], response.Values("WWW-Authenticate"));
        Assert.Null(response.Headers.Location);
    }

    // The same for an endpoint that asks only for a signed in user.
    [Fact]
    public async Task AnAnonymousCallToAnAuthenticatedOnlyEndpointAnswers401WithTheBearerChallenge()
    {
        using var response = await GetAsync(AccessHost.WhoAmIRoute, token: null);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
        Assert.Equal(["Bearer"], response.Values("WWW-Authenticate").Select(v => v.Split(' ')[0]));
        Assert.Null(response.Headers.Location);
    }

    // Breaks when a token the host cannot validate is accepted, or answered with a redirect or a server error.
    [Theory]
    [InlineData("not-a-token")]
    [InlineData("a.b.c")]
    public async Task AMalformedTokenAnswers401WithoutARedirect(string token)
    {
        using var response = await GetAsync(AccessHost.WhoAmIRoute, token);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
        Assert.Null(response.Headers.Location);
    }

    // Breaks when the signature is not checked against the configured key.
    [Fact]
    public async Task ATokenSignedWithAnotherKeyAnswers401()
    {
        var forged = AccessJwt.Token(Claims(), secret: "another-fake-key-of-more-than-32-bytes-0123456789");

        using var response = await GetAsync(AccessHost.WhoAmIRoute, forged);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // Breaks when the lifetime of the token is not validated.
    [Fact]
    public async Task AnExpiredTokenAnswers401()
    {
        var expired = AccessJwt.Token(Claims(), lifetime: TimeSpan.FromMinutes(-30));

        using var response = await GetAsync(AccessHost.WhoAmIRoute, expired);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // Breaks when the API accepts the library's own user claim, id, as the user: the claim the API reads is sub, and only sub.
    // A token that carries the user as id alone has no user, so it is not a valid login of this API.
    [Fact]
    public async Task ATokenWithTheUserOnlyInTheIdClaimAnswers401()
    {
        var token = AccessJwt.Token([new Claim("id", User.ToString("D")), new Claim("tenant_id", Tenant.ToString("D"))]);

        using var response = await GetAsync(AccessHost.WhoAmIRoute, token);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // The same refusal on an endpoint that needs a permission, not only on the one that needs a signed in user.
    [Fact]
    public async Task ATokenWithTheUserOnlyInTheIdClaimAnswers401OnAPermissionEndpointToo()
    {
        var token = AccessJwt.Token([new Claim("id", User.ToString("D")), new Claim("tenant_id", Tenant.ToString("D"))]);

        using var response = await GetAsync(AccessHost.ReadRoute, token);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // Control of the two tests above: the same token with sub is a user. Breaks when the host refuses every token.
    [Fact]
    public async Task ATokenWithSubAndTheTenantIsTheUserOfTheRequest()
    {
        using var response = await GetAsync(AccessHost.WhoAmIRoute, AccessJwt.TokenFor(User, Tenant));

        var seen = await response.ReadWhoAmIAsync();
        Assert.Equal(User, seen.UserId);
        Assert.Equal(Tenant, seen.TenantId);
    }

    private static Claim[] Claims() => [new("sub", User.ToString("D")), new("tenant_id", Tenant.ToString("D"))];
}
