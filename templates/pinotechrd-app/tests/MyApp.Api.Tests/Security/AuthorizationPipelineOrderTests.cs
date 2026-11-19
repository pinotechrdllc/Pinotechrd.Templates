using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Security;

// Where authentication, the steps and authorization sit among the other parts of the pipeline: the rate limiter and the
// forwarded headers before them, CORS and the security headers around them.
public class AuthorizationPipelineOrderTests
{
    private const string Listed = "https://app.example.com";

    private const string Proxy = "10.9.9.9";

    private const string AllHeaders = "Authorization, Content-Type";

    // Breaks when authentication (or a step) runs for a request the limiter rejects: a flood would cost a token validation
    // each. The third request of a client with two permits is a 429 and no authentication ran for it.
    [Fact]
    public async Task RateLimitingAnswers429BeforeAuthenticationAndTheStepsRun()
    {
        var counter = new Counter();
        using var factory = new ApiHostFactory().WithLowLimit().WithServices(services =>
        {
            services.AddSingleton(counter);
            services.AddAuthentication(CountingHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, CountingHandler>(CountingHandler.SchemeName, _ => { });
            TestAuthenticationHandler.MakeDefault(services, CountingHandler.SchemeName);
            services.AddAuthenticatedRequestStep<CountingStep>();
        });
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(ProblemFixtureEndpoint.Secured, "10.7.0.1");
        using var second = await client.SendAsync(ProblemFixtureEndpoint.Secured, "10.7.0.1");
        using var third = await client.SendAsync(ProblemFixtureEndpoint.Secured, "10.7.0.1");

        await ProblemAssert.IsProblemAsync(first, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(second, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(third, 429, "rate_limited");
        Assert.Equal(2, counter.Authentications);
        Assert.Equal(2, counter.Steps);
    }

    // Breaks when a request over the limit that sends no credentials is told it is unauthorized instead of rate limited:
    // the same with the placeholder scheme.
    [Fact]
    public async Task ARequestOverTheLimitWithNoCredentialsGets429NotAn401WithThePlaceholderScheme()
    {
        using var factory = new ApiHostFactory().WithLowLimit();
        using var client = factory.CreateClient();

        using var first = await client.SendAsync(ProblemFixtureEndpoint.AdminOnly, "10.7.0.2");
        using var second = await client.SendAsync(ProblemFixtureEndpoint.AdminOnly, "10.7.0.2");
        using var third = await client.SendAsync(ProblemFixtureEndpoint.AdminOnly, "10.7.0.2");

        Assert.Equal(HttpStatusCode.Unauthorized, first.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, second.StatusCode);
        await ProblemAssert.IsProblemAsync(third, 429, "rate_limited");
        SecurityAssert.RetryAfterSeconds(third);
        Assert.Empty(third.Values("WWW-Authenticate"));
    }

    // Breaks when the CORS preflight of a protected endpoint needs credentials (a browser never sends any with a preflight),
    // or when the browser is not allowed to send the Authorization header on the real request.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.Secured)]
    [InlineData(ProblemFixtureEndpoint.AdminOnly)]
    public async Task ThePreflightOfAProtectedEndpointSucceedsWithoutCredentialsAndAllowsTheAuthorizationHeader(string path)
    {
        using var factory = new ApiHostFactory().WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(Listed, "GET", AllHeaders, path);

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent, $"The preflight answered {(int)response.StatusCode}.");
        Assert.Empty(response.Values("WWW-Authenticate"));
        SecurityAssert.AllowsOrigin(response, Listed);
        Assert.Contains(
            "Authorization",
            (response.Value("Access-Control-Allow-Headers") ?? string.Empty).Split(',', StringSplitOptions.TrimEntries),
            StringComparer.OrdinalIgnoreCase);
        SecurityAssert.HasStandardHeaders(response);
    }

    // Breaks when the preflight is let through authorization only for some schemes: with a scheme of the application as the
    // default the preflight still needs no credentials.
    [Fact]
    public async Task ThePreflightOfAProtectedEndpointSucceedsWithARealSchemeToo()
    {
        using var factory = new ApiHostFactory().WithServices(TestAuthenticationHandler.Register).WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);
        using var client = factory.CreateClient();

        using var response = await client.PreflightAsync(Listed, "GET", AllHeaders, ProblemFixtureEndpoint.AdminOnly);

        Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NoContent, $"The preflight answered {(int)response.StatusCode}.");
        SecurityAssert.AllowsOrigin(response, Listed);
    }

    // Breaks when the real request after a preflight is not given the CORS headers on its 401 (the browser would hide the
    // status behind a CORS failure) or on its 403.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.Secured, false, 401)]
    [InlineData(ProblemFixtureEndpoint.AdminOnly, true, 403)]
    public async Task TheUnauthorizedAndForbiddenAnswersCarryTheCorsHeadersOfAListedOrigin(string path, bool authenticated, int status)
    {
        using var factory = new ApiHostFactory().WithServices(TestAuthenticationHandler.Register).WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path, origin: Listed, user: authenticated ? "alice" : null);

        Assert.Equal(status, (int)response.StatusCode);
        SecurityAssert.AllowsOrigin(response, Listed);
        SecurityAssert.HasStandardHeaders(response);
    }

    // Breaks when the forwarded headers are applied after the limiter or authentication: behind a trusted proxy each real
    // client has its own permits (the proxy's address would be shared), and HSTS follows the forwarded scheme on a 401.
    [Fact]
    public async Task ForwardedHeadersAreAppliedBeforeTheLimiterAndAuthentication()
    {
        using var factory = ApiHostFactory.ForEnvironment("Production")
            .WithLowLimit()
            .WithSetting(HttpSettingKeys.KnownProxy(0), Proxy);
        using var client = factory.ClientFor("http://api.example.com");

        using var a1 = await client.SendAsync(ProblemFixtureEndpoint.Secured, Proxy, forwardedFor: "203.0.113.1", forwardedProto: "https");
        using var a2 = await client.SendAsync(ProblemFixtureEndpoint.Secured, Proxy, forwardedFor: "203.0.113.1", forwardedProto: "https");
        using var a3 = await client.SendAsync(ProblemFixtureEndpoint.Secured, Proxy, forwardedFor: "203.0.113.1", forwardedProto: "https");
        using var b1 = await client.SendAsync(ProblemFixtureEndpoint.Secured, Proxy, forwardedFor: "203.0.113.2", forwardedProto: "https");

        await ProblemAssert.IsProblemAsync(a1, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(a2, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(a3, 429, "rate_limited");
        await ProblemAssert.IsProblemAsync(b1, 401, "unauthorized");
        SecurityAssert.HasHsts(a1);
        SecurityAssert.HasHsts(b1);
    }

    // Breaks when the exception handler does not wrap authentication and the steps: a scheme that fails is the plain 500
    // problem, with the security headers, and not an unhandled error of the server.
    [Fact]
    public async Task ASchemeThatThrowsIsRenderedAsTheInternalErrorProblemWithSecurityHeaders()
    {
        using var factory = new ApiHostFactory().WithServices(services =>
        {
            services.AddAuthentication(ThrowingHandler.SchemeName).AddScheme<AuthenticationSchemeOptions, ThrowingHandler>(ThrowingHandler.SchemeName, _ => { });
            TestAuthenticationHandler.MakeDefault(services, ThrowingHandler.SchemeName);
        });
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ProblemFixtureEndpoint.Secured);

        await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        SecurityAssert.HasStandardHeaders(response);
        Assert.DoesNotContain(ProblemFixtureEndpoint.Secret, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    private sealed class Counter
    {
        private int _authentications;
        private int _steps;

        public int Authentications => Volatile.Read(ref _authentications);

        public int Steps => Volatile.Read(ref _steps);

        public void Authenticated() => Interlocked.Increment(ref _authentications);

        public void Stepped() => Interlocked.Increment(ref _steps);
    }

    private sealed class CountingHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Counting";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            Context.RequestServices.GetRequiredService<Counter>().Authenticated();
            return Task.FromResult(AuthenticateResult.NoResult());
        }
    }

    private sealed class ThrowingHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Throwing";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            throw new InvalidOperationException(ProblemFixtureEndpoint.Secret);
    }

    private sealed class CountingStep(Counter counter) : IAuthenticatedRequestStep
    {
        public int Order => 0;

        public ValueTask<IResult?> InvokeAsync(HttpContext context)
        {
            counter.Stepped();
            return ValueTask.FromResult<IResult?>(null);
        }
    }
}
