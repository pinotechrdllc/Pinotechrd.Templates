using System.Net;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Access.Auth;

// The CSRF check of login, refresh and logout: a browser request from another site is refused before anything happens; a
// request that sends neither Origin nor Sec-Fetch-Site (not a browser) is not. These run on a host without a database: the
// check comes first, so "not refused" is any answer but 403.
public class CsrfTests
{
    private const string Own = "https://localhost";

    private const string Listed = "https://app.example.test";

    private static (string Name, string[] Values) Origin(params string[] values) => ("Origin", values);

    private static (string Name, string[] Values) Site(params string[] values) => ("Sec-Fetch-Site", values);

    private static ApiHostFactory Host() =>
        AccessHost.WithoutDatabase()
            .WithSetting(AccessHost.AuthPermitLimitKey, "600")
            .WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);

    private static IEnumerable<Func<HttpClient, (string, string[])[], Task<HttpResponseMessage>>> Endpoints() =>
    [
        (client, headers) => client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword, headers),
        (client, headers) => client.RefreshAsync(AccessWorld.SlugA, "a-cookie", headers),
        (client, headers) => client.LogoutAsync(AccessWorld.SlugA, "a-cookie", headers),
    ];

    private static async Task AssertAsync(bool refused, string describe, params (string Name, string[] Values)[] headers)
    {
        using var factory = Host();
        using var client = factory.AuthClient();
        foreach (var send in Endpoints())
        {
            using var response = await send(client, headers);
            if (refused)
            {
                await ProblemAssert.IsProblemAsync(response, 403, "csrf_rejected");
                Assert.Empty(response.SetCookies());
            }
            else
            {
                Assert.True(response.StatusCode != HttpStatusCode.Forbidden, $"{describe} was refused as csrf_rejected at {response.RequestMessage?.RequestUri?.AbsolutePath}.");
            }
        }
    }

    // Breaks when a foreign Origin is accepted, or the check is missing on one of the three endpoints.
    [Fact]
    public Task AForeignOriginIsRefused403CsrfRejectedOnAllThreeEndpoints() =>
        AssertAsync(true, "foreign origin", Origin("https://evil.example"));

    // Breaks when "null" (a sandboxed frame, a redirect chain) is taken as a match.
    [Fact]
    public Task TheNullOriginIsRefused() => AssertAsync(true, "null origin", Origin("null"));

    // Breaks when the own origin is compared without the scheme (an http page of the same host).
    [Fact]
    public Task AnOriginOfTheSameHostOnAnotherSchemeOrPortIsRefused() =>
        AssertAsync(true, "http origin", Origin("http://localhost"));

    // Breaks when the port is not part of the own origin.
    [Fact]
    public Task AnOriginOfTheSameHostOnAnotherPortIsRefused() =>
        AssertAsync(true, "port origin", Origin("https://localhost:8443"));

    // Breaks when a request from another site (or the same site but another origin) passes on Sec-Fetch-Site alone.
    [Theory]
    [InlineData("cross-site")]
    [InlineData("same-site")]
    [InlineData("bogus")]
    public Task ASecFetchSiteOtherThanSameOriginOrNoneIsRefused(string site) =>
        AssertAsync(true, "Sec-Fetch-Site " + site, Site(site));

    // Breaks when a good Origin hides a cross-site fetch.
    [Fact]
    public Task TheOwnOriginWithACrossSiteFetchIsRefused() =>
        AssertAsync(true, "own origin, cross-site", Origin(Own), Site("cross-site"));

    // Breaks when only one copy of a repeated header is read (the first can be good while the last is not, or the reverse).
    [Fact]
    public Task AnOriginGivenTwiceIsRefused() => AssertAsync(true, "two origins", Origin(Own, Own));

    // Breaks when only one copy of a repeated Sec-Fetch-Site is read.
    [Fact]
    public Task ASecFetchSiteGivenTwiceIsRefused() => AssertAsync(true, "two fetch sites", Site("same-origin", "same-origin"));

    // Breaks when a request that is the page's own is refused: a same-origin call, a direct navigation (none).
    [Fact]
    public async Task TheOwnOriginAndSameOriginOrNoneFetchesPass()
    {
        await AssertAsync(false, "own origin", Origin(Own));
        await AssertAsync(false, "same-origin", Site("same-origin"));
        await AssertAsync(false, "none", Site("none"));
        await AssertAsync(false, "own origin, same-origin", Origin(Own), Site("same-origin"));
    }

    // Breaks when a client that is not a browser (no Origin, no Sec-Fetch-Site: a script, a mobile app) is refused.
    [Fact]
    public Task ARequestWithNeitherHeaderPasses() => AssertAsync(false, "no headers");

    // Breaks when the allowed origins of Http:Cors:AllowedOrigins do not let their page call the endpoints (a frontend on
    // another origin), or when listing one origin lets in another, or lets in its cross-site fetch for a non-listed Origin.
    [Fact]
    public async Task AListedOriginPassesEvenWithASameSiteFetchAndAnUnlistedOneStillDoesNot()
    {
        await AssertAsync(false, "listed origin", Origin(Listed));
        await AssertAsync(false, "listed origin, same-site", Origin(Listed), Site("same-site"));
        await AssertAsync(true, "unlisted origin, same-site", Origin("https://other.example.test"), Site("same-site"));
        await AssertAsync(true, "a prefix of the listed origin", Origin(Listed + ".evil.example"));
    }

    // Breaks when the check refuses after the action: a refused request must not sign in, spend a refresh token or end a
    // session. Real database: the same requests with no header would each do their work.
    [Collection(PostgresContainer.Name)]
    public class WithADatabase(PostgresFixture postgres)
    {
        [Fact]
        public async Task ARefusedRequestDoesNothing()
        {
            using var world = await AccessWorld.StartAsync(postgres);
            await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
            using var client = world.Factory.AuthClient();
            var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
            var attacker = new[] { ("Origin", new[] { "https://evil.example" }), ("Sec-Fetch-Site", new[] { "cross-site" }) };

            using var login = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword, attacker);
            using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie, attacker);
            using var logout = await client.LogoutAsync(AccessWorld.SlugA, cookie, attacker);
            using var legitimate = await client.RefreshAsync(AccessWorld.SlugA, cookie);

            foreach (var response in new[] { login, refresh, logout })
            {
                await ProblemAssert.IsProblemAsync(response, 403, "csrf_rejected");
                Assert.Empty(response.SetCookies());
            }

            Assert.Equal(HttpStatusCode.OK, legitimate.StatusCode);
        }

        [Fact]
        public async Task AListedOriginCanSignIn()
        {
            using var world = await AccessWorld.StartAsync(postgres, f => f.WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed));
            await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
            using var client = world.Factory.AuthClient();

            using var login = await client.LoginAsync(
                AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword, ("Origin", [Listed]), ("Sec-Fetch-Site", ["same-site"]));

            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        }
    }
}
