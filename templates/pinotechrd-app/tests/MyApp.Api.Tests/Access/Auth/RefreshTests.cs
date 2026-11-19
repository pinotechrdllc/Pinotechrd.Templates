using System.Net;
using System.Text.Json;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using Pinotechrd.Security.Enums;

namespace MyApp.Api.Tests.Access.Auth;

// POST /api/v1/access/auth/refresh: no body, the refresh token only in the cookie.
[Collection(PostgresContainer.Name)]
public class RefreshTests(PostgresFixture postgres)
{
    // Breaks when a refresh does not rotate (same cookie again), puts the token in the body, or issues a token of another tenant.
    [Fact]
    public async Task RefreshAnswers200WithANewAccessTokenAndANewCookieAndTheTokenIsNotInTheBody()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, first) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var response = await client.RefreshAsync(AccessWorld.SlugA, first);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(["accessToken", "expiresAt"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var second = response.RefreshTokenOf();
        Assert.NotEqual(first, second);
        Assert.DoesNotContain(second, text, StringComparison.Ordinal);
        var cookie = response.RefreshCookie()!;
        Assert.Equal(["httponly", "max-age", "path", "samesite", "secure"], cookie.Attributes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(world.TenantA.ToString("D"), AuthRequests.ClaimOf(body.RootElement.GetProperty("accessToken").GetString()!, "tenant_id"));
    }

    // Breaks when the spent token is still accepted after rotation.
    [Fact]
    public async Task TheOldCookieIsRefused401AfterARotationAndTheCookieIsCleared()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, first) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        using var rotated = await client.RefreshAsync(AccessWorld.SlugA, first);
        Assert.Equal(HttpStatusCode.OK, rotated.StatusCode);

        using var replay = await client.RefreshAsync(AccessWorld.SlugA, first);

        await AuthScenarios.AssertRefreshRefusedAsync(replay);
    }

    // Breaks when a replayed token only fails itself: the session that came out of it must die too (token theft).
    [Fact]
    public async Task ReplayingASpentCookieRevokesTheNewCookieToo()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, first) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        using var rotated = await client.RefreshAsync(AccessWorld.SlugA, first);
        var second = rotated.RefreshTokenOf();
        using var replay = await client.RefreshAsync(AccessWorld.SlugA, first);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var newest = await client.RefreshAsync(AccessWorld.SlugA, second);

        await AuthScenarios.AssertRefreshRefusedAsync(newest);
    }

    // Breaks when the revocation of a replay reaches the other sessions of the user (a second device signed in on its own).
    [Fact]
    public async Task AReplayDoesNotRevokeAnotherSessionOfTheSameUser()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, first) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        var (_, other) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        using var rotated = await client.RefreshAsync(AccessWorld.SlugA, first);
        using var replay = await client.RefreshAsync(AccessWorld.SlugA, first);
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        using var otherSession = await client.RefreshAsync(AccessWorld.SlugA, other);

        Assert.Equal(HttpStatusCode.OK, otherSession.StatusCode);
    }

    // Breaks when the consume and the rotation are not atomic: two requests with the same cookie must not both get a session.
    [Fact]
    public async Task TwoParallelRefreshesOfOneCookieGiveExactlyOne200()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        for (var round = 0; round < 5; round++)
        {
            var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

            var responses = await Task.WhenAll(
                Enumerable.Range(0, 2).Select(_ => Task.Run(() => client.RefreshAsync(AccessWorld.SlugA, cookie), TestContext.Current.CancellationToken)));

            var statuses = responses.Select(r => (int)r.StatusCode).Order().ToArray();
            Assert.True(statuses.SequenceEqual([200, 401]), $"Round {round}: {string.Join(", ", statuses)}");
            foreach (var response in responses)
            {
                response.Dispose();
            }
        }
    }

    // Breaks when the refresh token is not bound to its tenant: a cookie of tenant A must be worthless for tenant B and
    // must not be spent by the attempt.
    [Fact]
    public async Task TenantACookieWithTenantBIsRefused401AndStillWorksInTenantA()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        await world.AddUserAsync(world.TenantB, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        using var foreign = await client.RefreshAsync(AccessWorld.SlugB, cookie);
        using var own = await client.RefreshAsync(AccessWorld.SlugA, cookie);

        await ProblemAssert.IsProblemAsync(foreign, 401, "invalid_refresh_token");
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
        Assert.Equal(world.TenantA.ToString("D"), AuthRequests.ClaimOf(await own.AccessTokenOfAsync(), "tenant_id"));
    }

    // Breaks when a refresh failure keeps the cookie alive, tells its cause, or is not 401 invalid_refresh_token: no cookie,
    // an unknown token, an unknown or malformed tenant.
    [Fact]
    public async Task EveryRefreshFailureIs401InvalidRefreshTokenThatClearsTheCookie()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        var attempts = new (string Slug, string? Cookie)[]
        {
            (AccessWorld.SlugA, null),
            (AccessWorld.SlugA, "not-a-token"),
            (AccessWorld.SlugA, cookie + "x"),
            ("nobody", cookie),
            ("A--b", cookie),
        };
        var prints = new List<string>();
        foreach (var (slug, value) in attempts)
        {
            using var response = await client.RefreshAsync(slug, value);
            await AuthScenarios.AssertRefreshRefusedAsync(response);
            prints.Add(await response.PrintAsync());
        }

        Assert.Single(prints.Distinct(StringComparer.Ordinal));
        using var still = await client.RefreshAsync(AccessWorld.SlugA, cookie);
        Assert.Equal(HttpStatusCode.OK, still.StatusCode);
    }

    // Breaks when a suspended tenant keeps its sessions alive, or when a suspension destroys them for good.
    [Fact]
    public async Task ASuspendedTenantCannotRefreshAndCanOnceActiveAgain()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Suspended);

        using var suspended = await client.RefreshAsync(AccessWorld.SlugA, cookie);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Active);
        using var active = await client.RefreshAsync(AccessWorld.SlugA, cookie);

        await AuthScenarios.AssertRefreshRefusedAsync(suspended);
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
    }

    // Breaks when a refresh keeps working for a user who was deactivated after the sign-in.
    [Fact]
    public async Task AnInactiveUserCannotRefresh()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        var (_, cookie) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        await AccessDatabase.SetUserActiveAsync(world.Services, world.TenantA, user, false);

        using var response = await client.RefreshAsync(AccessWorld.SlugA, cookie);

        await AuthScenarios.AssertRefreshRefusedAsync(response);
    }
}
