using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// Each module registers one checker for its permissions, keyed by its name; a router sends every permission to the checker of its
// first segment, and denies a permission whose module has none.
public class PermissionCheckerRoutingTests
{
    private const string Read = "Access.Users.Read";

    private const string Invoice = "Billing.Invoices.Read";

    private const string Orphan = "Orphans.Things.Read";

    private static ApiHostFactory Host() =>
        new ApiHostFactory()
            .WithServices(TestAuthenticationHandler.Register)
            .WithServices(services => services.AddPermissions(Read, Invoice, Orphan))
            .WithEndpoints(routes =>
            {
                routes.MapGet("/routing/access", () => Results.Ok()).RequirePermission(Read);
                routes.MapGet("/routing/billing", () => Results.Ok()).RequirePermission(Invoice);
                routes.MapGet("/routing/orphan", () => Results.Ok()).RequirePermission(Orphan);
            });

    private static string FailureNaming(ApiHostFactory factory, string needle)
    {
        var failure = StartupFailure.Capture(factory);
        var named = failure.All.FirstOrDefault(e => e.Message.Contains(needle, StringComparison.Ordinal));
        Assert.True(named is not null, $"Expected the start to fail naming '{needle}' but got: {failure.Thrown}");
        return named.Message;
    }

    // Breaks when the permission is not sent to the checker of its module, or with another string, or without the current user.
    [Fact]
    public async Task APermissionReachesTheCheckerOfItsFirstSegmentWithTheExactStringAndTheCurrentUser()
    {
        var access = new TestPermissionChecker(Read);
        using var factory = Host().WithPermissionChecker(access, "Access");
        using var client = factory.CreateClient();
        var user = Guid.Parse("9d3c1a52-6f0e-4f43-8d1b-2a7e5c9b0f11");

        using var response = await client.GetAsAsync("/api/v1/routing/access", "alice", IdentityRequests.ClaimsFor(user, IdentityRequests.TenantA));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(access.Calls);
        Assert.Equal(Read, call.Permission);
        Assert.Equal(user, call.UserId);
    }

    // Breaks when a permission of a module without a checker is sent to another module's checker (a router that falls back to
    // the first or the only one), or granted: it is denied, and no checker is asked.
    [Fact]
    public async Task APermissionOfAModuleWithoutACheckerAnswers403AndNoOtherCheckerIsAsked()
    {
        var access = new TestPermissionChecker(Read, Invoice, Orphan);
        using var factory = Host().WithPermissionChecker(access, "Access");
        using var client = factory.CreateClient();

        using var orphan = await client.GetAsAsync("/api/v1/routing/orphan", "alice");
        using var billing = await client.GetAsAsync("/api/v1/routing/billing", "alice");

        await ProblemAssert.IsProblemAsync(orphan, 403, "forbidden");
        await ProblemAssert.IsProblemAsync(billing, 403, "forbidden");
        Assert.Empty(access.Calls);
    }

    // Breaks when a module's checker is chosen by something other than the first segment exactly (a prefix, another case).
    [Fact]
    public async Task TheCheckerOfAModuleWhoseNameDiffersOnlyInCaseIsNotUsed()
    {
        var upper = new TestPermissionChecker(Read);
        using var factory = Host().WithPermissionChecker(upper, "ACCESS");
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync("/api/v1/routing/access", "alice");

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
        Assert.Empty(upper.Calls);
    }

    // Breaks when two modules share a checker's answers or questions: each is asked only its own permissions, and each
    // answers for its own.
    [Fact]
    public async Task TwoModulesEachGetOnlyTheirOwnPermissions()
    {
        var access = new TestPermissionChecker(Read, Invoice);
        var billing = new TestPermissionChecker(Invoice);
        using var factory = Host().WithPermissionChecker(access, "Access").WithPermissionChecker(billing, "Billing");
        using var client = factory.CreateClient();

        using var onAccess = await client.GetAsAsync("/api/v1/routing/access", "alice");
        using var onBilling = await client.GetAsAsync("/api/v1/routing/billing", "alice");

        Assert.Equal(HttpStatusCode.OK, onAccess.StatusCode);
        Assert.Equal(HttpStatusCode.OK, onBilling.StatusCode);
        Assert.Equal([Read], access.Calls.Select(c => c.Permission));
        Assert.Equal([Invoice], billing.Calls.Select(c => c.Permission));
    }

    // Breaks when a module whose checker denies is granted because another module's checker grants.
    [Fact]
    public async Task ADenyingCheckerOfOneModuleIsNotOverriddenByAnotherModulesCheckerThatGrants()
    {
        var access = new TestPermissionChecker();
        var billing = new TestPermissionChecker(Read, Invoice);
        using var factory = Host().WithPermissionChecker(access, "Access").WithPermissionChecker(billing, "Billing");
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync("/api/v1/routing/access", "alice");

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
        Assert.Empty(billing.Calls);
    }

    // Breaks when a host with no checker at all grants a permission, whatever its module.
    [Theory]
    [InlineData("/api/v1/routing/access")]
    [InlineData("/api/v1/routing/billing")]
    [InlineData("/api/v1/routing/orphan")]
    [InlineData(ProblemFixtureEndpoint.AdminOnly)]
    public async Task WithNoCheckerAtAllEveryPermissionAnswers403(string path)
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(path, "alice");

        await ProblemAssert.IsProblemAsync(response, 403, "forbidden");
    }

    // Breaks when a checker registered through the public method is not the module's: scoped, resolved per request.
    [Fact]
    public async Task ACheckerRegisteredWithAddPermissionCheckerServesItsModule()
    {
        using var factory = Host().WithServices(services => services.AddPermissionChecker<GrantingChecker>("Access"));
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync("/api/v1/routing/access", "alice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when two checkers for one module are accepted: which one answers would depend on registration order.
    [Theory]
    [InlineData("Access", "Access")]
    [InlineData("Access", "ACCESS")]
    [InlineData("Sales2", "SALES2")]
    public void TwoCheckersForOneModuleIgnoringCaseStopTheStartAndBothAreNamed(string first, string second)
    {
        using var factory = Host().WithServices(services =>
        {
            services.AddKeyedScoped<IPermissionChecker, GrantingChecker>(first);
            services.AddKeyedScoped<IPermissionChecker, DenyingChecker>(second);
        });

        var message = FailureNaming(factory, nameof(GrantingChecker));

        Assert.Contains(nameof(DenyingChecker), message, StringComparison.Ordinal);
        Assert.Contains(first, message, StringComparison.OrdinalIgnoreCase);
    }

    // Breaks when a checker registered as IPermissionChecker itself is accepted: it would replace the routing to the modules.
    [Fact]
    public void ACheckerRegisteredAsIPermissionCheckerItselfStopsTheStartAndIsNamed()
    {
        using var factory = Host().WithServices(services => services.AddScoped<IPermissionChecker, GrantingChecker>());

        var message = FailureNaming(factory, nameof(GrantingChecker));

        Assert.Contains("AddPermissionChecker", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACheckerRegisteredAsAnInstanceOfIPermissionCheckerItselfStopsTheStart()
    {
        using var factory = Host().WithServices(services => services.AddSingleton<IPermissionChecker>(new DenyingChecker()));

        FailureNaming(factory, nameof(DenyingChecker));
    }

    // Breaks when a key that is not a module name is accepted: a lower-case start, a separator, an empty text, a key that is not
    // text at all.
    [Theory]
    [InlineData("access")]
    [InlineData("Access.Users")]
    [InlineData("Acc ess")]
    [InlineData("")]
    public void AKeyThatIsNotAModuleNameStopsTheStart(string key)
    {
        using var factory = Host().WithServices(services => services.AddKeyedScoped<IPermissionChecker, GrantingChecker>(key));

        var message = FailureNaming(factory, "is not a module name");

        Assert.Contains(nameof(GrantingChecker), message, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyThatIsNotTextStopsTheStart()
    {
        using var factory = Host().WithServices(services => services.AddKeyedScoped<IPermissionChecker, GrantingChecker>(42));

        FailureNaming(factory, "is not a module name");
    }

    // Breaks when the public registration accepts a name it would later have to refuse at the start.
    [Theory]
    [InlineData("access")]
    [InlineData("Access.Users")]
    [InlineData("")]
    [InlineData("Ac cess")]
    public void AddPermissionCheckerRefusesAnInvalidModuleName(string module)
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddPermissionChecker<GrantingChecker>(module));
    }

    [Fact]
    public void AddPermissionCheckerAcceptsAModuleNameAndReturnsTheCollection()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddPermissionChecker<GrantingChecker>("Access"));
    }

    // Offenders are reported together with the rest: one message for a clash and a stray checker.
    [Fact]
    public void ACheckerProblemIsReportedTogetherWithAnEndpointProblem()
    {
        using var factory = Host()
            .WithServices(services => services.AddScoped<IPermissionChecker, GrantingChecker>())
            .WithEndpoints(routes => routes.MapGet("/routing/undeclared", () => Results.Ok()));

        var message = FailureNaming(factory, "/api/v1/routing/undeclared");

        Assert.Contains(nameof(GrantingChecker), message, StringComparison.Ordinal);
    }

    private sealed class GrantingChecker : IPermissionChecker
    {
        public ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class DenyingChecker : IPermissionChecker
    {
        public ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken) => ValueTask.FromResult(false);
    }
}
