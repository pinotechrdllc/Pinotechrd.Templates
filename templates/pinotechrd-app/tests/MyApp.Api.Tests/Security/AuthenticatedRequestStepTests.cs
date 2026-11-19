using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Application.Identity;
using MyApp.BuildingBlocks.Application.Security;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// The slot between authentication and authorization: IAuthenticatedRequestStep implementations run in ascending order, for
// every request that matched an endpoint except the health probes, and may answer the request themselves.
public class AuthenticatedRequestStepTests
{
    private const string PermissionRoute = "/api/v1/step-fixtures/permission";

    private const string OpenRoute = "/api/v1/step-fixtures/open";

    private const string Granted = "Fixture.Steps.Run";

    private static readonly Dictionary<string, string> RejectHeader = new() { [RejectingStep.Header] = "1" };

    private static Dictionary<string, string> ThrowHeader => new() { [ThrowingStep.Header] = "1" };

    // A host with the test scheme, a permission endpoint and an anonymous one that log what reaches them, a checker that
    // logs and denies, and steps registered in an order that is not their execution order.
    private static (ApiHostFactory Factory, StepLog Log) Host(Action<IServiceCollection>? more = null)
    {
        var log = new StepLog();
        var factory = new ApiHostFactory()
            .WithServices(TestAuthenticationHandler.Register)
            .WithServices(services =>
            {
                services.AddSingleton(log);
                services.AddPermissionChecker<LoggingChecker>("Fixture");
                services.AddPermissions(Granted);
                services.AddAuthenticatedRequestStep<ThirdStep>();
                services.AddAuthenticatedRequestStep<FirstStep>();
                services.AddAuthenticatedRequestStep<SecondStep>();
                more?.Invoke(services);
            })
            .WithEndpoints(routes =>
            {
                routes.MapGet("/step-fixtures/permission", (StepLog entries) => Record(entries, "endpoint")).RequirePermission(Granted);
                routes.MapGet("/step-fixtures/open", (StepLog entries) => Record(entries, "endpoint")).AllowAnonymous();
            });
        return (factory, log);
    }

    private static IResult Record(StepLog log, string entry)
    {
        log.Add(entry);
        return Results.Ok();
    }

    // Breaks when steps do not run in ascending order whatever the order of registration, when they do not run after
    // authentication (the principal is not yet there) and the built-in steps (the user and the tenant are not yet set), or
    // when authorization runs before them (the checker would be asked first). The endpoint runs last.
    [Fact]
    public async Task StepsRunInTheirDeclaredOrderAfterAuthenticationAndBeforeAuthorization()
    {
        var (factory, log) = Host();
        using var _ = factory;
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(PermissionRoute, "alice");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["first", "second", "third", "checker", "endpoint"], log.Names);
        var expectedUser = TestAuthenticationHandler.UserIdOf("alice");
        Assert.All(log.Entries.Where(e => e.StartsWith("first", StringComparison.Ordinal) || e.StartsWith("second", StringComparison.Ordinal) || e.StartsWith("third", StringComparison.Ordinal)), entry =>
        {
            Assert.Contains("|authenticated=True|", entry, StringComparison.Ordinal);
            Assert.Contains($"|user={expectedUser}|", entry, StringComparison.Ordinal);
            Assert.Contains($"|tenant={TestAuthenticationHandler.DefaultTenant}|", entry, StringComparison.Ordinal);
            Assert.Contains($"|route={PermissionRoute}", entry, StringComparison.Ordinal);
        });
    }

    // Breaks when a step is skipped for an anonymous caller (the slot would not be usable for a limit or a log that must
    // see everybody), or when anonymous means a different order or sees an identity.
    [Fact]
    public async Task StepsRunForAnAnonymousRequestToo()
    {
        var (factory, log) = Host();
        using var _ = factory;
        using var client = factory.CreateClient();

        using var anonymousEndpoint = await client.SendAsync(OpenRoute);
        using var protectedEndpoint = await client.SendAsync(PermissionRoute);

        Assert.Equal(HttpStatusCode.OK, anonymousEndpoint.StatusCode);
        await ProblemAssert.IsProblemAsync(protectedEndpoint, 401, "unauthorized");
        Assert.Equal(["first", "second", "third", "endpoint", "first", "second", "third"], log.Names);
        Assert.All(log.Entries.Where(e => e != "endpoint"), entry =>
        {
            Assert.Contains("|authenticated=False|", entry, StringComparison.Ordinal);
            Assert.Contains("|user=none|", entry, StringComparison.Ordinal);
            Assert.Contains("|tenant=none|", entry, StringComparison.Ordinal);
        });
    }

    // Breaks when a step that answers does not stop the request: the authorization (a 403 would hide the 400), the endpoint
    // and the later steps must not run. The caller here has no permission, so the answer is a 400 only if authorization
    // did not get its turn.
    [Fact]
    public async Task AStepAnswering400PreventsAuthorizationTheEndpointAndTheLaterSteps()
    {
        var (factory, log) = Host(services => services.AddAuthenticatedRequestStep<RejectingStep>());
        using var _ = factory;
        using var client = factory.CreateClient();
        log.Grant = false;

        using var response = await client.SendAsync(HttpMethod.Get, PermissionRoute, "alice", headers: RejectHeader);

        await ProblemAssert.IsProblemAsync(response, 400, "bad_request");
        Assert.Equal(["first", "rejecting"], log.Names);
    }

    // Breaks when the answer of a step is held back until the caller is authenticated: a step runs for an anonymous request
    // too, so it answers it with its own status and not the 401 of authorization.
    [Fact]
    public async Task AStepAnswering400AnswersAnAnonymousRequestToAProtectedEndpointWith400NotWith401()
    {
        var (factory, log) = Host(services => services.AddAuthenticatedRequestStep<RejectingStep>());
        using var _ = factory;
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(HttpMethod.Get, PermissionRoute, headers: RejectHeader);

        await ProblemAssert.IsProblemAsync(response, 400, "bad_request");
        Assert.DoesNotContain("checker", log.Names);
        Assert.DoesNotContain("endpoint", log.Names);
    }

    // Breaks when the 401 a step asks for with a challenge is not the 401 unauthorized problem, or when it does not stop
    // the request.
    [Fact]
    public async Task AStepThatChallengesAnswers401UnauthorizedAndStopsTheRequest()
    {
        var (factory, log) = Host(services => services.AddAuthenticatedRequestStep<ChallengingStep>());
        using var _ = factory;
        using var client = factory.CreateClient();
        var headers = new Dictionary<string, string> { [ChallengingStep.Header] = "1" };

        using var response = await client.SendAsync(HttpMethod.Get, PermissionRoute, "alice", headers: headers);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
        Assert.DoesNotContain("checker", log.Names);
        Assert.DoesNotContain("endpoint", log.Names);
    }

    // Breaks when two steps with the same order are accepted: their relative order would depend on registration, and the
    // application would behave differently between a build and the next.
    [Fact]
    public void TwoStepsWithTheSameOrderStopTheStartAndBothAreNamed()
    {
        var (factory, _) = Host(services =>
        {
            services.AddAuthenticatedRequestStep<SecondStep>();
            services.AddAuthenticatedRequestStep<SameOrderAsSecondStep>();
        });
        using var _ = factory;

        var failure = StartupFailure.Capture(factory);

        var named = failure.All.FirstOrDefault(e =>
            e.Message.Contains(nameof(SecondStep), StringComparison.Ordinal) && e.Message.Contains(nameof(SameOrderAsSecondStep), StringComparison.Ordinal));
        Assert.True(named is not null, $"Expected a failure naming both steps but got: {failure.Thrown}");
    }

    // Breaks when a step runs for a request that has nothing to authorize or that must answer whoever asks: the probes
    // of the orchestrator, an unknown route, a wrong method. A matched API endpoint is the positive control.
    [Fact]
    public async Task StepsDoNotRunForHealthEndpointsUnknownRoutesOrWrongMethods()
    {
        var (factory, log) = Host();
        using var _ = factory;
        using var client = factory.CreateClient();

        using var live = await client.SendAsync("/health/live");
        using var ready = await client.SendAsync("/health/ready");
        using var unknown = await client.SendAsync(SecurityRequests.Unknown);
        using var unknownOutsideTheApi = await client.SendAsync("/no-such-route");
        using var wrongMethod = await client.SendAsync(HttpMethod.Post, SecurityRequests.Version, "alice");
        using var wrongMethodOnProtected = await client.SendAsync(HttpMethod.Post, PermissionRoute, "alice");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownOutsideTheApi.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethod.StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, wrongMethodOnProtected.StatusCode);
        Assert.Empty(log.Entries);

        using var control = await client.SendAsync(SecurityRequests.Version);
        Assert.Equal(HttpStatusCode.OK, control.StatusCode);
        Assert.Equal(["first", "second", "third"], log.Names);
    }

    // Breaks when a step that fails is not rendered as the plain 500 internal_error, leaks the text of its exception, or
    // lets the request go on to authorization and the endpoint.
    [Fact]
    public async Task AStepThatThrowsAnswers500InternalErrorWithoutTheExceptionText()
    {
        var (factory, log) = Host(services => services.AddAuthenticatedRequestStep<ThrowingStep>());
        using var _ = factory;
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(HttpMethod.Get, PermissionRoute, "alice", headers: ThrowHeader);

        await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        var body = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(ProblemFixtureEndpoint.Secret, body, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), body, StringComparison.Ordinal);
        Assert.DoesNotContain("checker", log.Names);
        Assert.DoesNotContain("endpoint", log.Names);
    }

    // Breaks when a step registered twice runs twice per request (documented: it runs once).
    [Fact]
    public async Task AStepRegisteredTwiceRunsOnce()
    {
        var (factory, log) = Host(services => services.AddAuthenticatedRequestStep<FirstStep>());
        using var _ = factory;
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(OpenRoute);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["first", "second", "third", "endpoint"], log.Names);
    }

    // Breaks when the built-in steps stop running before the application's: a step of the application with the lowest
    // order the platform allows to it (zero) sees the user and the tenant of the request set (asserted above), and the
    // orders of the built-in steps stay below zero.
    [Fact]
    public void TheBuiltInStepsHaveNegativeOrdersInTheDocumentedSequence()
    {
        Assert.Equal(-300, AuthenticatedRequestStepOrder.CurrentUser);
        Assert.Equal(-200, AuthenticatedRequestStepOrder.Tenant);
        Assert.Equal(-100, AuthenticatedRequestStepOrder.Telemetry);
    }

    private sealed class StepLog
    {
        private readonly ConcurrentQueue<string> _entries = new();

        /// <summary>Whether the checker grants the permission; a test that must see the 403 that did not happen turns it off.</summary>
        public bool Grant { get; set; } = true;

        public IReadOnlyList<string> Entries => [.. _entries];

        /// <summary>The first part of each entry: what happened, without what the step saw.</summary>
        public IReadOnlyList<string> Names => [.. _entries.Select(e => e.Split('|')[0])];

        public void Add(string entry) => _entries.Enqueue(entry);
    }

    private abstract class RecordingStep(StepLog log, string name, int order) : IAuthenticatedRequestStep
    {
        public int Order => order;

        public virtual ValueTask<IResult?> InvokeAsync(HttpContext context)
        {
            var services = context.RequestServices;
            var user = services.GetRequiredService<ICurrentUser>();
            var tenant = services.GetRequiredService<ICurrentTenant>();
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;
            log.Add(
                $"{name}|authenticated={context.User.Identities.Any(i => i.IsAuthenticated)}"
                + $"|user={(user.IsAuthenticated ? user.UserId.ToString() : "none")}"
                + $"|tenant={(tenant.IsSet ? tenant.TenantId.ToString() : "none")}"
                + $"|route={route}");
            return ValueTask.FromResult<IResult?>(null);
        }
    }

    private sealed class FirstStep(StepLog log) : RecordingStep(log, "first", 0);

    private sealed class SecondStep(StepLog log) : RecordingStep(log, "second", 5);

    private sealed class ThirdStep(StepLog log) : RecordingStep(log, "third", 10);

    private sealed class SameOrderAsSecondStep(StepLog log) : RecordingStep(log, "same-order", 5);

    // Order 3: after the first step and before the second, so the second never runs when this one answers.
    private sealed class RejectingStep(StepLog log) : IAuthenticatedRequestStep
    {
        public const string Header = "X-Reject-Step";

        public int Order => 3;

        public ValueTask<IResult?> InvokeAsync(HttpContext context)
        {
            if (!context.Request.Headers.ContainsKey(Header))
            {
                return ValueTask.FromResult<IResult?>(null);
            }

            log.Add("rejecting");
            return ValueTask.FromResult<IResult?>(Results.StatusCode(StatusCodes.Status400BadRequest));
        }
    }

    private sealed class ChallengingStep : IAuthenticatedRequestStep
    {
        public const string Header = "X-Challenge-Step";

        public int Order => 3;

        public ValueTask<IResult?> InvokeAsync(HttpContext context) =>
            ValueTask.FromResult<IResult?>(context.Request.Headers.ContainsKey(Header) ? TypedResults.Challenge() : null);
    }

    private sealed class ThrowingStep : IAuthenticatedRequestStep
    {
        public const string Header = "X-Throw-Step";

        public int Order => 3;

        public ValueTask<IResult?> InvokeAsync(HttpContext context) =>
            context.Request.Headers.ContainsKey(Header)
                ? throw new InvalidOperationException(ProblemFixtureEndpoint.Secret)
                : ValueTask.FromResult<IResult?>(null);
    }

    private sealed class LoggingChecker(StepLog log) : IPermissionChecker
    {
        public ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken)
        {
            log.Add("checker");
            return ValueTask.FromResult(log.Grant);
        }
    }
}
