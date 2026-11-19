using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// Endpoints may not name their own authentication schemes (the pipeline authenticates once, with the default scheme), and a
// token whose tenant claim is not exactly one valid tenant never yields a user on an anonymous endpoint.
public class EndpointSchemesAndTenantClaimTests
{
    private const string Granted = "Fixture.Schemes.Run";

    private static readonly Guid Alice = Guid.Parse("44444444-4444-4444-8444-444444444444");

    private static ApiHostFactory Host() =>
        new ApiHostFactory()
            .WithServices(TestAuthenticationHandler.Register)
            .WithServices(services => services.AddPermissions(Granted));

    private static string FailureNaming(ApiHostFactory factory, string needle)
    {
        var failure = StartupFailure.Capture(factory);
        var named = failure.All.FirstOrDefault(e => e.Message.Contains(needle, StringComparison.Ordinal));
        Assert.True(named is not null, $"Expected the start to fail naming '{needle}' but got: {failure.Thrown}");
        return named.Message;
    }

    // ---- 1. Own authentication schemes stop the start ----

    // Breaks when an endpoint that asks for its own scheme through an authorize attribute is accepted, or when the failure
    // does not say which endpoint and which setting.
    [Fact]
    public void AnAuthenticatedOnlyEndpointWithItsOwnSchemesStopsTheStartAndIsNamed()
    {
        using var factory = Host().WithEndpoints(routes =>
            routes.MapGet("/scheme-fixtures/own", () => Results.Ok())
                .AuthenticatedOnly()
                .RequireAuthorization(new AuthorizeAttribute { AuthenticationSchemes = "Key" }));

        var message = FailureNaming(factory, "AuthenticationSchemes");

        Assert.Contains("GET /api/v1/scheme-fixtures/own", message, StringComparison.Ordinal);
    }

    // Breaks when the check only looks at AuthenticatedOnly endpoints and lets a permission endpoint with a policy that names
    // schemes through.
    [Fact]
    public void APermissionEndpointWithAPolicyThatNamesSchemesStopsTheStartAndIsNamed()
    {
        using var factory = Host()
            .WithServices(services => services.AddAuthorization(options =>
                options.AddPolicy("KeyOnly", policy => policy.AddAuthenticationSchemes("Key").RequireAssertion(_ => true))))
            .WithEndpoints(routes =>
                routes.MapGet("/scheme-fixtures/policy", () => Results.Ok())
                    .RequirePermission(Granted)
                    .RequireAuthorization("KeyOnly"));

        var message = FailureNaming(factory, "AuthenticationSchemes");

        Assert.Contains("GET /api/v1/scheme-fixtures/policy", message, StringComparison.Ordinal);
    }

    // Control. Breaks when the check rejects every endpoint (the host would never start).
    [Fact]
    public async Task EndpointsWithoutOwnSchemesStillStartAndAreServed()
    {
        using var factory = Host().WithEndpoints(routes =>
        {
            routes.MapGet("/scheme-fixtures/plain", () => Results.Ok()).AuthenticatedOnly();
            routes.MapGet("/scheme-fixtures/perm", () => Results.Ok()).RequirePermission(Granted);
        });
        using var client = factory.CreateClient();

        using var plain = await client.GetAsAsync("/api/v1/scheme-fixtures/plain", "alice");
        using var anonymous = await client.SendAsync("/api/v1/scheme-fixtures/plain");

        Assert.Equal(HttpStatusCode.OK, plain.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    // ---- 2. A tenant claim that is not exactly one valid tenant ----

    private sealed class SelectTenantBStep : IAuthenticatedRequestStep
    {
        public int Order => AuthenticatedRequestStepOrder.TenantSelection + 1; // after the X-Tenant step, which holds the order itself

        public ValueTask<IResult?> InvokeAsync(HttpContext context)
        {
            context.RequestServices.GetRequiredService<ICurrentTenantInitializer>().Initialize(IdentityRequests.TenantB);
            return ValueTask.FromResult<IResult?>(null);
        }
    }

    private static ApiHostFactory TenantSelectionHost() =>
        Host()
            .WithServices(services => services.AddAuthenticatedRequestStep<SelectTenantBStep>())
            .WithEndpoints(routes =>
            {
                routes.MapGet("/scheme-fixtures/login", (ICurrentUser user) => Results.Ok(new { user = user.IsAuthenticated ? user.UserId.ToString() : null }))
                    .AllowAnonymous();
                routes.MapGet("/scheme-fixtures/private", () => Results.Ok()).AuthenticatedOnly();
            });

    private static async Task<(HttpStatusCode Status, string? User)> LoginAsync(HttpClient client, string claims)
    {
        using var response = await client.SendAsync(HttpMethod.Get, "/api/v1/scheme-fixtures/login", user: "alice", claims: claims);
        var status = response.StatusCode;
        if (status != HttpStatusCode.OK)
        {
            return (status, null);
        }

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        var user = json.RootElement.GetProperty("user");
        return (status, user.ValueKind == JsonValueKind.Null ? null : user.GetString());
    }

    // Breaks when a token without tenant claim keeps its user on an anonymous endpoint while another tenant was selected
    // (the user would be taken as a member of tenant B), or when the request is rejected instead of served anonymously.
    [Fact]
    public async Task AnAnonymousEndpointServesATokenWithoutTenantClaimAsNotAuthenticated()
    {
        using var factory = TenantSelectionHost();
        using var client = factory.CreateClient();

        var (status, user) = await LoginAsync(client, TestAuthenticationHandler.ClaimsOf(("sub", Alice.ToString("D"))));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(user);
    }

    // Breaks when a duplicated tenant claim is read as one tenant (the first, the last) instead of as no tenant.
    [Fact]
    public async Task AnAnonymousEndpointServesATokenWithADuplicatedTenantClaimAsNotAuthenticated()
    {
        using var factory = TenantSelectionHost();
        using var client = factory.CreateClient();
        var claims = TestAuthenticationHandler.ClaimsOf(
            ("sub", Alice.ToString("D")),
            ("tenant_id", IdentityRequests.TenantA.ToString("D")),
            ("tenant_id", IdentityRequests.TenantA.ToString("D")));

        var (status, user) = await LoginAsync(client, claims);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(user);
    }

    // Breaks when an unparseable tenant claim is ignored (the user kept) or throws (a 500).
    [Fact]
    public async Task AnAnonymousEndpointServesATokenWithAnUnparseableTenantClaimAsNotAuthenticated()
    {
        using var factory = TenantSelectionHost();
        using var client = factory.CreateClient();
        var claims = TestAuthenticationHandler.ClaimsOf(("sub", Alice.ToString("D")), ("tenant_id", "not-a-guid"));

        var (status, user) = await LoginAsync(client, claims);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(user);
    }

    // Control. Breaks when the tenant mismatch (token tenant A, selected tenant B) stops being a 401, which would make the
    // three tests above pass for the wrong reason.
    [Fact]
    public async Task ATokenForTenantAWhileTenantBIsSelectedStillAnswers401()
    {
        using var factory = TenantSelectionHost();
        using var client = factory.CreateClient();

        var (status, _) = await LoginAsync(client, IdentityRequests.ClaimsFor(Alice, IdentityRequests.TenantA));

        Assert.Equal(HttpStatusCode.Unauthorized, status);
    }

    // Breaks when the fix for anonymous endpoints also lets a token without tenant claim through to an authenticated-only one.
    [Fact]
    public async Task AnAuthenticatedOnlyEndpointStillAnswers401ToATokenWithoutTenantClaim()
    {
        using var factory = TenantSelectionHost();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(
            HttpMethod.Get,
            "/api/v1/scheme-fixtures/private",
            user: "alice",
            claims: TestAuthenticationHandler.ClaimsOf(("sub", Alice.ToString("D"))));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
