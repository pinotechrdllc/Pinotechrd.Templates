using System.Net;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Access.Auth;

// The `auth` rate limit counts the four endpoints together, per client, on top of the global limit.
[Collection(PostgresContainer.Name)]
public class AuthRateLimitTests(PostgresFixture postgres)
{
    // Breaks when the endpoints each have their own counter (a client could send the limit to each), when one of the four
    // is outside the limit, or when the limit is not the configured one.
    [Fact]
    public async Task LoginRefreshLogoutAndChangePasswordCountTogetherAgainstTheConfiguredLimit()
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f.WithSetting(AccessHost.AuthPermitLimitKey, "5"));
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        var (token, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email); // 1
        using var refresh = await client.RefreshAsync(AccessWorld.SlugA, cookie); // 2
        using var logout = await client.LogoutAsync(AccessWorld.SlugA, cookie); // 3
        using var change = await client.ChangePasswordAsync(token, "Not-the-current-password", "N3w-passw0rd-for-tests"); // 4
        using var failed = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, "Wr0ng-password-for-test"); // 5

        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, change.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, failed.StatusCode);

        using var login6 = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);
        using var refresh6 = await client.RefreshAsync(AccessWorld.SlugA, cookie);
        using var logout6 = await client.LogoutAsync(AccessWorld.SlugA, cookie);
        using var change6 = await client.ChangePasswordAsync(token, AccessDatabase.DefaultPassword, "N3w-passw0rd-for-tests");

        foreach (var response in new[] { login6, refresh6, logout6, change6 })
        {
            await ProblemAssert.IsProblemAsync(response, 429, "rate_limited");
            Assert.InRange(SecurityAssert.RetryAfterSeconds(response), 1, 60);
        }
    }

    // Breaks when the limit of the auth endpoints is applied to the rest of the API (the version route), which has its own.
    [Fact]
    public async Task ExhaustingTheAuthLimitDoesNotLimitOtherRoutes()
    {
        using var world = await AccessWorld.StartAsync(postgres, f => f.WithSetting(AccessHost.AuthPermitLimitKey, "1"));
        using var client = world.Factory.AuthClient();
        using var first = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, "x");
        using var limited = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, "x");

        using var version = await client.GetAsync(SecurityRequests.Version, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.OK, version.StatusCode);
    }
}
