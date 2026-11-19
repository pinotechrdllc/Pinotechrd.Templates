using System.Net;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// The API is closed unless an endpoint says otherwise. With no real authentication scheme (the placeholder one, which
// authenticates nobody) an anonymous request to a protected endpoint is a 401 problem; the anonymous endpoints, the
// unknown routes and the wrong methods answer as before.
public class DenyByDefaultTests
{
    private const string Listed = "https://app.example.com";

    private static readonly string[] ProtectedPaths = [ProblemFixtureEndpoint.Secured, ProblemFixtureEndpoint.AdminOnly];

    // Breaks when an anonymous request to a protected endpoint is answered with anything but the 401 unauthorized problem
    // document: another status (500 for want of a scheme, 403, 200), another code, another media type, no trace id.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.Secured)]
    [InlineData(ProblemFixtureEndpoint.AdminOnly)]
    public async Task AnonymousRequestToAProtectedEndpointAnswers401UnauthorizedAsAProblemWithTheBearerChallenge(string path)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path);

        var problem = await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        Assert.Equal(["Bearer"], response.Values("WWW-Authenticate"));
        SecurityAssert.HasStandardHeaders(response);
        Assert.Contains("no-store", response.Value("Cache-Control"), StringComparison.Ordinal);
    }

    // Breaks when the 401 of the placeholder scheme loses a response header the pipeline gives every answer: the 401 is
    // written inside the security headers and CORS, in production too.
    [Fact]
    public async Task TheUnauthorizedAnswerInProductionCarriesHstsAndCors()
    {
        using var factory = ApiHostFactory.ForEnvironment("Production").WithSetting(HttpSettingKeys.AllowedOrigin(0), Listed);
        using var client = factory.ClientFor("https://api.example.com");

        using var response = await client.SendAsync(ProblemFixtureEndpoint.Secured, origin: Listed);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
        SecurityAssert.HasStandardHeaders(response);
        SecurityAssert.HasHsts(response);
        SecurityAssert.AllowsOrigin(response, Listed);
    }

    // Breaks when a request that sends credentials the placeholder cannot understand reaches the endpoint: nobody can log in
    // yet, whatever the Authorization header says.
    [Theory]
    [InlineData("Bearer a.b.c")]
    [InlineData("Basic YWxpY2U6c2VjcmV0")]
    [InlineData("Bearer ")]
    public async Task ACredentialSentToThePlaceholderSchemeStillAnswers401(string authorization)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(
            HttpMethod.Get, ProblemFixtureEndpoint.AdminOnly, headers: new Dictionary<string, string> { ["Authorization"] = authorization });

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
        // The bearer scheme of the Access module says why it refused a token (error="invalid_token"); the scheme stays Bearer.
        Assert.StartsWith("Bearer", Assert.Single(response.Values("WWW-Authenticate")), StringComparison.Ordinal);
    }

    // Breaks when an endpoint that must stay open to anyone (the version, the probes of the orchestrator, the OpenAPI
    // document) is closed by the default.
    [Theory]
    [InlineData(SecurityRequests.Version)]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/openapi/v1.json")]
    [InlineData(ProblemFixtureEndpoint.Cached)]
    public async Task AnonymousEndpointsStayOpenToAnonymousCallers(string path)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Values("WWW-Authenticate"));
    }

    // Breaks when the probes or the version need credentials to be answered, or are closed for a caller that sends a token
    // that the application cannot validate (an orchestrator or a monitor never sends one).
    [Theory]
    [InlineData(SecurityRequests.Version)]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task AnonymousEndpointsAnswerACallerThatSendsAnInvalidToken(string path)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(
            HttpMethod.Get, path, headers: new Dictionary<string, string> { ["Authorization"] = "Bearer not-a-token" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when the fallback policy turns an unknown route into 401, which hides that the route does not exist and breaks
    // clients that tell 404 from 401. The same under /api/v1 and below a protected fixture route.
    [Theory]
    [InlineData("/no-such-route")]
    [InlineData("/api/v1/no-such-route")]
    [InlineData("/api/v1/problem-fixtures/no-such-route")]
    [InlineData("/api/v1/problem-fixtures/secured/extra")]
    [InlineData("/api/v2/version")]
    [InlineData("/")]
    public async Task AnUnknownRouteAnswers404NotFoundToAnAnonymousCaller(string path)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(path);

        await ProblemAssert.IsProblemAsync(response, 404, "not_found");
        Assert.Empty(response.Values("WWW-Authenticate"));
    }

    // Breaks when the platform's 405 endpoint (it has no route and no metadata, so the fallback policy would apply to it) is
    // turned into 401. The wrong method on a protected route is a 405 as well: the request matched no endpoint of its own.
    [Theory]
    [InlineData("POST", SecurityRequests.Version)]
    [InlineData("PUT", SecurityRequests.Version)]
    [InlineData("POST", ProblemFixtureEndpoint.Secured)]
    [InlineData("DELETE", ProblemFixtureEndpoint.AdminOnly)]
    [InlineData("POST", "/health/live")]
    public async Task AWrongMethodAnswers405MethodNotAllowedToAnAnonymousCaller(string method, string path)
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(new HttpMethod(method), path);

        await ProblemAssert.IsProblemAsync(response, 405, "method_not_allowed");
        Assert.Empty(response.Values("WWW-Authenticate"));
    }

    // The same for a caller that is logged in: a 404 and a 405 do not depend on who asks.
    [Fact]
    public async Task UnknownRoutesAndWrongMethodsAnswerTheSameToAnAuthenticatedCaller()
    {
        using var factory = new ApiHostFactory().WithServices(TestAuthenticationHandler.Register);
        using var client = factory.CreateClient();

        using var unknown = await client.GetAsAsync("/api/v1/no-such-route", "alice");
        using var wrongMethod = await client.SendAsync(HttpMethod.Post, ProblemFixtureEndpoint.AdminOnly, user: "alice");

        await ProblemAssert.IsProblemAsync(unknown, 404, "not_found");
        await ProblemAssert.IsProblemAsync(wrongMethod, 405, "method_not_allowed");
    }

    // Breaks when the exemption for unknown routes and wrong methods is applied to a routed endpoint: a request that
    // matched a protected endpoint is never let through.
    [Fact]
    public async Task ARoutedProtectedEndpointIsNeverLetThroughByTheExemptionForUnknownRoutes()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        foreach (var path in ProtectedPaths)
        {
            using var response = await client.SendAsync(path);

            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
    }

    // Breaks when the closed default of the API depends on the start check alone: with the check taken out of the host (as a
    // bug in it would), an endpoint under /api/v1 that declares nothing still answers an anonymous caller 401. The check is
    // found by its type name, it being internal.
    [Fact]
    public async Task AnUndeclaredEndpointIsStillClosedToAnonymousCallersWhenTheStartCheckIsNotThere()
    {
        using var factory = new ApiHostFactory()
            .WithServices(services =>
            {
                foreach (var descriptor in services.Where(d => d.ServiceType == typeof(Microsoft.AspNetCore.Hosting.IStartupFilter)
                    && d.ImplementationType?.Name == "AccessDeclarationCheck").ToList())
                {
                    services.Remove(descriptor);
                }
            })
            .WithEndpoints(routes => routes.MapGet("/deny-fixtures/undeclared", () => Results.Ok()));
        using var client = factory.CreateClient();

        using var response = await client.SendAsync("/api/v1/deny-fixtures/undeclared");

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // ---- The placeholder scheme (criterion 10) ----

    // Breaks when the placeholder scheme is not the default while no other is registered: authorization would fail for want
    // of a scheme and answer 500 instead of 401.
    [Fact]
    public async Task WithNoOtherSchemeAProtectedEndpointAnswers401NotAServerError()
    {
        using var factory = new ApiHostFactory();
        using var client = factory.CreateClient();

        using var response = await client.SendAsync(ProblemFixtureEndpoint.Secured);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // Breaks when the placeholder stays the default after the application registers a real scheme as the default
    // (AddAuthentication(name)): the real scheme would never authenticate anybody, or two schemes would both challenge.
    [Fact]
    public async Task ARealDefaultSchemeReplacesThePlaceholderSoNobodyIsChallengedTwice()
    {
        using var factory = new ApiHostFactory().WithServices(RegisterChallengingScheme(configureDefaults: false));
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync(ProblemFixtureEndpoint.Secured);
        using var authenticated = await client.SendAsync(ProblemFixtureEndpoint.Secured, user: "alice");

        await ProblemAssert.IsProblemAsync(anonymous, 401, "unauthorized");
        Assert.Equal([ChallengingHandler.Challenge], anonymous.Values("WWW-Authenticate"));
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    // Breaks when the placeholder is the default although the application set the authenticate and challenge schemes
    // separately, which the documentation lists as a way to replace it.
    [Fact]
    public async Task ARealAuthenticateAndChallengeSchemePairReplacesThePlaceholder()
    {
        using var factory = new ApiHostFactory().WithServices(RegisterChallengingScheme(configureDefaults: true));
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync(ProblemFixtureEndpoint.Secured);
        using var authenticated = await client.SendAsync(ProblemFixtureEndpoint.Secured, user: "alice");

        await ProblemAssert.IsProblemAsync(anonymous, 401, "unauthorized");
        Assert.Equal([ChallengingHandler.Challenge], anonymous.Values("WWW-Authenticate"));
        Assert.Equal(HttpStatusCode.OK, authenticated.StatusCode);
    }

    // Breaks when a challenge scheme set on its own is overridden by the placeholder: the placeholder would answer the
    // challenge and the application's scheme would never be asked.
    [Fact]
    public async Task ARealChallengeSchemeAloneIsTheOneThatChallenges()
    {
        using var factory = new ApiHostFactory().WithServices(services =>
        {
            services.AddAuthentication(options => options.DefaultChallengeScheme = ChallengingHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, ChallengingHandler>(ChallengingHandler.SchemeName, _ => { });
        });
        using var client = factory.CreateClient();

        using var anonymous = await client.SendAsync(ProblemFixtureEndpoint.Secured);

        await ProblemAssert.IsProblemAsync(anonymous, 401, "unauthorized");
        Assert.Equal([ChallengingHandler.Challenge], anonymous.Values("WWW-Authenticate"));
    }

    // configureDefaults false: the challenging scheme is the default scheme, and the test scheme authenticates. True: no
    // default scheme at all, only the authenticate scheme and the challenge scheme, set apart.
    private static Action<IServiceCollection> RegisterChallengingScheme(bool configureDefaults) =>
        services =>
        {
            if (configureDefaults)
            {
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                        options.DefaultChallengeScheme = ChallengingHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { })
                    .AddScheme<AuthenticationSchemeOptions, ChallengingHandler>(ChallengingHandler.SchemeName, _ => { });
            }
            else
            {
                services.AddAuthentication(ChallengingHandler.SchemeName)
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(TestAuthenticationHandler.SchemeName, _ => { })
                    .AddScheme<AuthenticationSchemeOptions, ChallengingHandler>(ChallengingHandler.SchemeName, _ => { });
                services.Configure<AuthenticationOptions>(options =>
                {
                    options.DefaultAuthenticateScheme = TestAuthenticationHandler.SchemeName;
                    // The Access module sets challenge and forbid defaults of its own, which win over DefaultScheme.
                    options.DefaultChallengeScheme = ChallengingHandler.SchemeName;
                    options.DefaultForbidScheme = ChallengingHandler.SchemeName;
                });
            }
        };

    /// <summary>A scheme of the application: it authenticates nobody and challenges with a header of its own.</summary>
    private sealed class ChallengingHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "Challenger";

        public const string Challenge = "Challenger realm=\"test\"";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers.WWWAuthenticate = Challenge;
            return Task.CompletedTask;
        }
    }
}
