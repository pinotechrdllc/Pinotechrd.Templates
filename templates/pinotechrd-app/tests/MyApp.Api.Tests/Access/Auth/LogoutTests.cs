using System.Net;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Access.Auth;

// POST /api/v1/access/auth/logout: always 204, always drops the cookie.
[Collection(PostgresContainer.Name)]
public class LogoutTests(PostgresFixture postgres)
{
    // Breaks when the logout does not end the session (the cookie stays good) or does not drop the cookie.
    [Fact]
    public async Task LogoutAnswers204ClearsTheCookieAndTheCookieIsDead()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var logout = await client.LogoutAsync(AccessWorld.SlugA, cookie);
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);

        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        AuthScenarios.AssertCleared(logout);
        await AuthScenarios.AssertRefreshRefusedAsync(refresh);
    }

    // Breaks when a logout without a session, with junk, or for another tenant is an error (it would reveal what is valid) or
    // does not drop the cookie.
    [Fact]
    public async Task LogoutWithoutAValidCookieStillAnswers204AndClearsTheCookie()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        foreach (var (slug, cookie) in new (string, string?)[]
        {
            (AccessWorld.SlugA, null),
            (AccessWorld.SlugA, "not-a-token"),
            ("nobody", "not-a-token"),
        })
        {
            using var response = await client.LogoutAsync(slug, cookie);

            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            AuthScenarios.AssertCleared(response);
        }
    }

    // Breaks when the logout ends other sessions of the user, or a session of another tenant (A's cookie sent to B).
    [Fact]
    public async Task LogoutEndsOnlyItsOwnSessionAndATenantBLogoutDoesNotEndTenantAs()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, mine) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        var (_, other) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var foreign = await client.LogoutAsync(AccessWorld.SlugB, mine);
        using var own = await client.LogoutAsync(AccessWorld.SlugA, mine);
        using var otherSession = await client.RefreshAsync(AccessWorld.SlugA, other);

        Assert.Equal(HttpStatusCode.NoContent, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.OK, otherSession.StatusCode);
    }

    // Breaks when the foreign logout really ends the session: A's cookie must still be alive after B's logout.
    [Fact]
    public async Task ALogoutInTenantBLeavesTheSessionOfTenantAAlive()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var foreign = await client.LogoutAsync(AccessWorld.SlugB, cookie);
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);

        Assert.Equal(HttpStatusCode.NoContent, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }
    // Breaks when a logout that is refused before it runs (no tenant header: 400) still drops the cookie or ends the session.
    [Fact]
    public async Task ALogoutWithoutTheTenantHeaderIs400SetsNoCookieAndKeepsTheSession()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var logout = await client.LogoutAsync(null, cookie);
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);

        await ProblemAssert.IsProblemAsync(logout, 400, "bad_request");
        Assert.Empty(logout.SetCookies());
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    // Breaks when a logout refused by the CSRF check still drops the cookie or ends the session.
    [Fact]
    public async Task ACsrfRefusedLogoutSetsNoCookieAndKeepsTheSession()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var logout = await client.LogoutAsync(AccessWorld.SlugA, cookie, ("Sec-Fetch-Site", ["cross-site"]));
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);

        await ProblemAssert.IsProblemAsync(logout, 403, "csrf_rejected");
        Assert.Empty(logout.SetCookies());
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }

    // Breaks when a logout that the rate limit refuses still drops the cookie or ends the session. The window is short so the
    // session can be checked once it has passed.
    [Fact]
    public async Task ARateLimitedLogoutSetsNoCookieAndKeepsTheSession()
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f
            .WithSetting(AccessHost.AuthPermitLimitKey, "2")
            .WithSetting("Http:RateLimiting:Auth:Window", "00:00:04"));
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        using var filler = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, "Wr0ng-password-for-test");

        using var logout = await client.LogoutAsync(AccessWorld.SlugA, cookie);

        await ProblemAssert.IsProblemAsync(logout, 429, "rate_limited");
        Assert.Empty(logout.SetCookies());
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie);
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
    }
}
