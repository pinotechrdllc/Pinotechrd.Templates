using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// Identity claims count only from the authenticated identities of the principal, and a tenant chosen before the token's
// (the reserved tenant selection step) must be the token's own.
public class PrincipalReadingTests
{
    private static readonly Guid User = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static readonly Guid OtherUser = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private static ApiHostFactory Host() =>
        new ApiHostFactory().WithServices(services =>
            {
                services.AddAuthentication(MultiIdentityHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, MultiIdentityHandler>(MultiIdentityHandler.SchemeName, _ => { });
                TestAuthenticationHandler.MakeDefault(services, MultiIdentityHandler.SchemeName);
            })
            .WithIdentityFixtures();

    private static Task<HttpResponseMessage> AsAsync(HttpClient client, string path, string mode) =>
        client.SendAsync(HttpMethod.Get, path, headers: new Dictionary<string, string> { [MultiIdentityHandler.ModeHeader] = mode });

    // Breaks when the identity is read from an identity that is not authenticated: the reviewer's case, an authenticated
    // principal with an empty authenticated identity and a second, unauthenticated one carrying valid `sub` and `tenant_id`,
    // would be a user of that tenant although nothing authenticated says so.
    [Fact]
    public async Task ClaimsOfAnUnauthenticatedIdentityDoNotCountAndAnAuthenticatedEmptyOneIsAnswered401()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        using var protectedEndpoint = await AsAsync(client, IdentityFixtureEndpoint.WhoAmIRoute, "empty-plus-unauthenticated-claims");
        using var permissionEndpoint = await AsAsync(client, ProblemFixtureEndpoint.AdminOnly, "empty-plus-unauthenticated-claims");

        await ProblemAssert.IsProblemAsync(protectedEndpoint, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(permissionEndpoint, 401, "unauthorized");
    }

    // On an anonymous endpoint the same principal has no user and no tenant.
    [Fact]
    public async Task ClaimsOfAnUnauthenticatedIdentityGiveNoUserAndNoTenantOnAnAnonymousEndpoint()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await AsAsync(client, IdentityFixtureEndpoint.OpenWhoAmIRoute, "empty-plus-unauthenticated-claims");

        var seen = await response.ReadWhoAmIAsync();
        Assert.True(seen.PrincipalAuthenticated);
        Assert.False(seen.UserAuthenticated);
        Assert.False(seen.TenantSet);
    }

    // Breaks when the claims of an unauthenticated identity are mixed with the authenticated one's: the identity is the
    // authenticated one alone.
    [Fact]
    public async Task OnlyTheAuthenticatedIdentityNamesTheUserAndTheTenant()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await AsAsync(client, IdentityFixtureEndpoint.WhoAmIRoute, "authenticated-plus-unauthenticated-claims");

        var seen = await response.ReadWhoAmIAsync();
        Assert.Equal(User, seen.UserId);
        Assert.Equal(IdentityRequests.TenantA, seen.TenantId);
    }

    // Breaks when the second authenticated identity's claim is ignored: two subjects are ambiguous, as two claims in one identity are.
    [Fact]
    public async Task TheSameClaimInTwoAuthenticatedIdentitiesIsTwoClaimsAndAnswers401()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await AsAsync(client, IdentityFixtureEndpoint.WhoAmIRoute, "two-authenticated-subjects");

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // ---- 9. The tenant selection step ----

    // Breaks when the token's tenant is not checked against a tenant chosen before it (a token for Y would work on the tenant
    // X selected), or when the mismatch is an error of the server instead of a 401.
    [Fact]
    public async Task ATokenForTheSelectedTenantContinuesAndATokenForAnotherGets401Never500()
    {
        using var factory = new ApiHostFactory()
            .WithServices(TestAuthenticationHandler.Register)
            .WithServices(services => services.AddAuthenticatedRequestStep<SelectTenantAStep>())
            .WithIdentityFixtures();
        using var client = factory.CreateClient();

        using var same = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", IdentityRequests.ClaimsFor(User, IdentityRequests.TenantA));
        using var other = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", IdentityRequests.ClaimsFor(User, IdentityRequests.TenantB));
        using var again = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", IdentityRequests.ClaimsFor(User, IdentityRequests.TenantA));

        var seen = await same.ReadWhoAmIAsync();
        Assert.Equal(IdentityRequests.TenantA, seen.TenantId);
        await ProblemAssert.IsProblemAsync(other, 401, "unauthorized");
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
    }

    // The mismatch on a protected permission endpoint is the same 401, and the checker is not asked.
    [Fact]
    public async Task ATokenForAnotherTenantThanTheSelectedOneIsNotAskedToTheChecker()
    {
        var checker = new TestPermissionChecker(ProblemFixtureEndpoint.AdminPermission);
        using var factory = new ApiHostFactory()
            .WithServices(TestAuthenticationHandler.Register)
            .WithServices(services => services.AddAuthenticatedRequestStep<SelectTenantAStep>())
            .WithPermissionChecker(checker);
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice", IdentityRequests.ClaimsFor(User, IdentityRequests.TenantB));

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
        Assert.Empty(checker.Calls);
    }

    // The selection of a tenant on an anonymous endpoint (a login) stays: the step's tenant is kept, with no token.
    [Fact]
    public async Task ATenantSelectedOnAnAnonymousEndpointIsKept()
    {
        using var factory = new ApiHostFactory()
            .WithServices(TestAuthenticationHandler.Register)
            .WithServices(services => services.AddAuthenticatedRequestStep<SelectTenantAStep>())
            .WithIdentityFixtures();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(IdentityFixtureEndpoint.OpenWhoAmIRoute);

        var seen = await response.ReadWhoAmIAsync();
        Assert.Equal(IdentityRequests.TenantA, seen.TenantId);
        Assert.False(seen.UserAuthenticated);
    }

    [Fact]
    public void TheTenantSelectionOrderIsReservedBetweenTheUserAndTheTenantOfTheToken()
    {
        Assert.Equal(-250, AuthenticatedRequestStepOrder.TenantSelection);
        Assert.InRange(AuthenticatedRequestStepOrder.TenantSelection, AuthenticatedRequestStepOrder.CurrentUser + 1, AuthenticatedRequestStepOrder.Tenant - 1);
    }

    // Selects TenantA for every request, at the reserved order.
    private sealed class SelectTenantAStep : IAuthenticatedRequestStep
    {
        public int Order => AuthenticatedRequestStepOrder.TenantSelection;

        public ValueTask<IResult?> InvokeAsync(HttpContext context)
        {
            context.RequestServices.GetRequiredService<ICurrentTenantInitializer>().Initialize(IdentityRequests.TenantA);
            _ = context.RequestServices.GetRequiredService<ICurrentTenant>();
            return ValueTask.FromResult<IResult?>(null);
        }
    }

    /// <summary>A scheme that builds principals with several identities, chosen by a request header.</summary>
    private sealed class MultiIdentityHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Multi";

        public const string ModeHeader = "X-Test-Mode";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(ModeHeader, out var mode))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var tenant = IdentityRequests.TenantA.ToString("D");
            ClaimsIdentity[] identities = mode.ToString() switch
            {
                "empty-plus-unauthenticated-claims" =>
                [
                    new ClaimsIdentity(SchemeName),
                    new ClaimsIdentity([new Claim("sub", User.ToString("D")), new Claim("tenant_id", tenant)]),
                ],
                "authenticated-plus-unauthenticated-claims" =>
                [
                    new ClaimsIdentity([new Claim("sub", User.ToString("D")), new Claim("tenant_id", tenant)], SchemeName),
                    new ClaimsIdentity([new Claim("sub", OtherUser.ToString("D")), new Claim("tenant_id", IdentityRequests.TenantB.ToString("D"))]),
                ],
                "two-authenticated-subjects" =>
                [
                    new ClaimsIdentity([new Claim("sub", User.ToString("D")), new Claim("tenant_id", tenant)], SchemeName),
                    new ClaimsIdentity([new Claim("sub", OtherUser.ToString("D"))], SchemeName),
                ],
                _ => throw new InvalidOperationException("Unknown mode."),
            };
            var principal = new ClaimsPrincipal(identities);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName)));
        }
    }
}
