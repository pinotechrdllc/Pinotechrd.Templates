using System.Globalization;
using System.Net;
using System.Text.Json;
using MyApp.Api.Tests.Database;
using MyApp.Api.Tests.Support;
using Pinotechrd.Security.Enums;

namespace MyApp.Api.Tests.Access.Auth;

// POST /api/v1/access/auth/login on a real PostgreSQL database with tenants A and B and the real host.
[Collection(PostgresContainer.Name)]
public class LoginTests(PostgresFixture postgres)
{
    private const string WrongPassword = "Wr0ng-password-for-test";

    // Breaks when the token is not the one of the tenant that was asked for, the answer lacks the expiry, carries more than
    // the access token and the expiry (the refresh token must never be in the body), or the token does not open the API.
    [Fact]
    public async Task LoginInTenantAAnswers200WithATokenOfTenantAAndNothingElseInTheBody()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        using var response = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        using var body = JsonDocument.Parse(text);
        Assert.Equal(["accessToken", "expiresAt"], body.RootElement.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        var token = body.RootElement.GetProperty("accessToken").GetString()!;
        Assert.Equal(world.TenantA.ToString("D"), AuthRequests.ClaimOf(token, "tenant_id"));
        Assert.Equal(user.ToString("D"), AuthRequests.ClaimOf(token, "sub"));
        Assert.True(body.RootElement.GetProperty("expiresAt").GetDateTimeOffset() > DateTimeOffset.UtcNow);
        Assert.DoesNotContain(response.RefreshTokenOf(), text, StringComparison.Ordinal);
        using var whoAmI = await client.GetWithBearerAsync(AccessHost.WhoAmIRoute, token);
        Assert.Equal(HttpStatusCode.OK, whoAmI.StatusCode);
    }

    // Breaks when the same email in tenant B gets a token of tenant A (the tenant of the token is the one of the header).
    [Fact]
    public async Task TheSameEmailInTenantBGetsATokenOfTenantB()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        await world.AddUserAsync(world.TenantB, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        var (token, _) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugB, AuthScenarios.Email);

        Assert.Equal(world.TenantB.ToString("D"), AuthRequests.ClaimOf(token, "tenant_id"));
    }

    // Breaks when the cookie is missing, readable by scripts, sent over plain HTTP or to other sites, scoped to a path or domain
    // (which a browser refuses for a __Host- cookie), or its lifetime is not the sliding lifetime of the refresh token (7 days).
    [Fact]
    public async Task TheRefreshCookieHasExactlyTheHostPrefixAttributesAndTheSlidingLifetime()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        using var response = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);

        var cookie = Assert.Single(response.SetCookies());
        Assert.Equal("__Host-refresh", cookie.Name);
        Assert.False(string.IsNullOrEmpty(cookie.Value));
        Assert.Equal(["httponly", "max-age", "path", "samesite", "secure"], cookie.Attributes.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("/", cookie["path"]);
        Assert.Equal("strict", cookie["samesite"]?.ToLowerInvariant());
        Assert.Equal(TimeSpan.FromDays(7).TotalSeconds, double.Parse(cookie["max-age"]!, CultureInfo.InvariantCulture));
    }

    // Breaks when two sign-ins share a refresh token.
    [Fact]
    public async Task EachLoginIssuesAnotherRefreshToken()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        var (_, first) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        var (_, second) = await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);

        Assert.NotEqual(first, second);
    }

    // Breaks when a failure tells its cause: the wrong tenant, an unknown user, a wrong password, a locked account, an
    // inactive one and a suspended tenant must all be the same answer (status, headers, body but for the trace id) with no cookie.
    [Fact]
    public async Task EveryLoginFailureIsTheSame401InvalidCredentialsWithoutACookie()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.EmailOf(1));
        var locked = await world.AddUserAsync(world.TenantA, AuthScenarios.EmailOf(2));
        var inactive = await world.AddUserAsync(world.TenantA, AuthScenarios.EmailOf(3));
        await AccessDatabase.SetLockoutEndAsync(world.Services, world.TenantA, locked, DateTimeOffset.UtcNow.AddMinutes(10));
        await AccessDatabase.SetUserActiveAsync(world.Services, world.TenantA, inactive, false);
        await world.AddUserAsync(world.TenantB, AuthScenarios.EmailOf(4));
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantB, TenantStatus.Suspended);
        using var client = world.Factory.AuthClient();
        var password = AccessDatabase.DefaultPassword;

        var attempts = new (string Name, string Slug, string Email, string Password)[]
        {
            ("wrong password", AccessWorld.SlugA, AuthScenarios.EmailOf(1), WrongPassword),
            ("unknown user", AccessWorld.SlugA, "nobody@example.test", password),
            ("another tenant's slug", AccessWorld.SlugB, AuthScenarios.EmailOf(1), password),
            ("unknown slug", "nobody", AuthScenarios.EmailOf(1), password),
            ("malformed slug", "A--b", AuthScenarios.EmailOf(1), password),
            ("locked account, right password", AccessWorld.SlugA, AuthScenarios.EmailOf(2), password),
            ("inactive user, right password", AccessWorld.SlugA, AuthScenarios.EmailOf(3), password),
            ("suspended tenant, right password", AccessWorld.SlugB, AuthScenarios.EmailOf(4), password),
        };
        var prints = new List<(string Name, string Print)>();
        foreach (var (name, slug, email, pass) in attempts)
        {
            using var response = await client.LoginAsync(slug, email, pass);
            await ProblemAssert.IsProblemAsync(response, 401, "invalid_credentials");
            Assert.Empty(response.SetCookies());
            prints.Add((name, await response.PrintAsync()));
        }

        var distinct = prints.Select(p => p.Print).Distinct(StringComparer.Ordinal).ToArray();
        Assert.True(distinct.Length == 1, "The answers differ:\n" + string.Join("\n---\n", prints.Select(p => p.Name + "\n" + p.Print)));
    }

    // Breaks when a missing body field is let through to the password check instead of refused as invalid input, or sets a cookie.
    [Fact]
    public async Task ALoginWithoutEmailOrPasswordIsAnswered400ValidationFailedWithoutACookie()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        using var client = world.Factory.AuthClient();

        using var noPassword = await client.PostAsync(AuthRequests.Login, AccessWorld.SlugA, new { email = AuthScenarios.Email });
        using var empty = await client.PostAsync(AuthRequests.Login, AccessWorld.SlugA, new { });

        foreach (var response in new[] { noPassword, empty })
        {
            await ProblemAssert.IsProblemAsync(response, 400, "validation_failed");
            Assert.Empty(response.SetCookies());
        }
    }

    // Breaks when the lockout is not after 10 failures in a row, does not hold the correct password out, or never ends.
    [Fact]
    public async Task TenWrongPasswordsLockTheAccountEvenForTheRightPasswordUntilTheLockoutEnds()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        var user = await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        for (var i = 0; i < 10; i++)
        {
            using var wrong = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, WrongPassword);
            await ProblemAssert.IsProblemAsync(wrong, 401, "invalid_credentials");
        }

        using var locked = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);
        await ProblemAssert.IsProblemAsync(locked, 401, "invalid_credentials");
        Assert.Empty(locked.SetCookies());
        var (_, _, lockoutEnd) = await AccessDatabase.SecurityStateAsync(world.Services, world.TenantA, user);
        Assert.NotNull(lockoutEnd);
        Assert.InRange(lockoutEnd.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(14), TimeSpan.FromMinutes(15.1));

        // Identity reads the real clock: the end of the lockout is moved into the past instead.
        await AccessDatabase.SetLockoutEndAsync(world.Services, world.TenantA, user, DateTimeOffset.UtcNow.AddSeconds(-1));
        using var after = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);

        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    // Breaks when the lockout starts too early (nine failures) or a good login does not reset the count.
    [Fact]
    public async Task NineWrongPasswordsDoNotLockAndASuccessfulLoginResetsTheCount()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        for (var i = 0; i < 9; i++)
        {
            using var wrong = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, WrongPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
        for (var i = 0; i < 9; i++)
        {
            using var wrong = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, WrongPassword);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        }

        await AuthScenarios.SignInAsync(client, AccessWorld.SlugA, AuthScenarios.Email);
    }

    // Breaks when a request without X-Tenant is answered as "no tenant" (a 401 that hides the caller's mistake) instead of the
    // tenancy step's 400, or sets a cookie.
    [Fact]
    public async Task ALoginWithoutTheTenantHeaderIsAnswered400BadRequestWithoutACookie()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();

        using var response = await client.LoginAsync(null, AuthScenarios.Email, AccessDatabase.DefaultPassword);

        await ProblemAssert.IsProblemAsync(response, 400, "bad_request");
        Assert.Empty(response.SetCookies());
    }

    // Breaks when a suspended tenant keeps signing in, or when it stays unusable after it is active again (a cached status).
    [Fact]
    public async Task ASuspendedTenantCannotLogInAndCanOnceActiveAgain()
    {
        using var world = await AccessWorld.StartAsync(postgres);
        await world.AddUserAsync(world.TenantA, AuthScenarios.Email);
        using var client = world.Factory.AuthClient();
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Suspended);

        using var suspended = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);
        await AccessDatabase.SetTenantStatusAsync(world.Services, world.TenantA, TenantStatus.Active);
        using var active = await client.LoginAsync(AccessWorld.SlugA, AuthScenarios.Email, AccessDatabase.DefaultPassword);

        await ProblemAssert.IsProblemAsync(suspended, 401, "invalid_credentials");
        Assert.Empty(suspended.SetCookies());
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
    }
}
