using System.Diagnostics;
using System.Security.Claims;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web.Telemetry;
using Serilog.Events;

namespace MyApp.Api.Tests.Telemetry;

// The opaque identifiers of the user and the tenant (GUIDs) are on the completion event of an authenticated request
// (`UserId`, `TenantId`) and on its server span (`user.id`, `tenant.id`); an anonymous request, or one rejected with 401,
// has neither. Nothing else of the principal ever is: no name, no email, no token, no other claim.
public class IdentityTelemetryTests
{
    private static readonly Guid User = Guid.Parse("c0ffee00-0000-4000-8000-000000000001");

    private static readonly Guid Tenant = Guid.Parse("c0ffee00-0000-4000-8000-000000000002");

    private static ApiHostFactory Host() => new ApiHostFactory().WithServices(TestAuthenticationHandler.Register).WithIdentityFixtures();

    private sealed record Observed(LogEvent Completion, CapturedSpan Span, int Status);

    private static async Task<Observed> RequestAsync(
        ApiHostFactory factory, CollectingSink sink, TelemetryCapture capture, string path, string? user = null, string? claims = null)
    {
        using var client = factory.CreateClient();
        var traceId = ActivityTraceId.CreateRandom().ToHexString();
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.TryAddWithoutValidation("traceparent", $"00-{traceId}-{ActivitySpanId.CreateRandom().ToHexString()}-01");
        if (user is not null)
        {
            request.Headers.TryAddWithoutValidation(TestAuthenticationHandler.UserHeader, user);
        }

        if (claims is not null)
        {
            request.Headers.TryAddWithoutValidation(TestAuthenticationHandler.ClaimsHeader, claims);
        }

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        await TelemetryCapture.UntilAsync(() => capture.Spans.Any(s => s.TraceId == traceId && s.Kind == ActivityKind.Server));
        var span = Assert.Single(capture.Spans, s => s.TraceId == traceId && s.Kind == ActivityKind.Server);
        var completion = Assert.Single(sink.CompletionEvents(), e => e.TraceIdOf() == traceId);
        return new Observed(completion, span, (int)response.StatusCode);
    }

    private static void AssertIdentity(Observed observed, Guid user, Guid tenant)
    {
        Assert.Equal(user.ToString("D"), observed.Completion.ScalarOf(RequestLogging.UserIdProperty));
        Assert.Equal(tenant.ToString("D"), observed.Completion.ScalarOf(RequestLogging.TenantIdProperty));
        Assert.Equal(user.ToString("D"), observed.Span.Tags[TelemetryRegistration.UserIdTag]);
        Assert.Equal(tenant.ToString("D"), observed.Span.Tags[TelemetryRegistration.TenantIdTag]);
    }

    private static void AssertNoIdentity(Observed observed)
    {
        Assert.False(observed.Completion.Properties.ContainsKey(RequestLogging.UserIdProperty), "The completion event has a user id.");
        Assert.False(observed.Completion.Properties.ContainsKey(RequestLogging.TenantIdProperty), "The completion event has a tenant id.");
        Assert.False(observed.Span.Tags.ContainsKey(TelemetryRegistration.UserIdTag), "The server span has a user id.");
        Assert.False(observed.Span.Tags.ContainsKey(TelemetryRegistration.TenantIdTag), "The server span has a tenant id.");
    }

    [Fact]
    public void TheNamesOfTheIdentityPropertyAndAttributesAreTheDocumentedOnes()
    {
        Assert.Equal("UserId", RequestLogging.UserIdProperty);
        Assert.Equal("TenantId", RequestLogging.TenantIdProperty);
        Assert.Equal("user.id", TelemetryRegistration.UserIdTag);
        Assert.Equal("tenant.id", TelemetryRegistration.TenantIdTag);
    }

    // Breaks when the identifiers are missing from the event or the span of an authenticated request, are not the GUIDs of
    // the token in their standard text form, or are written under other names.
    [Theory]
    [InlineData(IdentityFixtureEndpoint.WhoAmIRoute)]
    [InlineData(ProblemFixtureEndpoint.Secured)]
    [InlineData(IdentityFixtureEndpoint.OpenWhoAmIRoute)]
    public async Task AnAuthenticatedRequestCarriesTheUserAndTheTenantOnTheEventAndTheSpan(string path)
    {
        using var factory = Host();
        var sink = factory.CaptureLogs();
        using var capture = factory.CaptureTelemetry();

        var observed = await RequestAsync(factory, sink, capture, path, "alice", IdentityRequests.ClaimsFor(User, Tenant));

        Assert.Equal(200, observed.Status);
        AssertIdentity(observed, User, Tenant);
    }

    // Breaks when an identity is recorded for a request that has none: an anonymous endpoint without credentials.
    [Fact]
    public async Task AnAnonymousRequestCarriesNeitherTheUserNorTheTenant()
    {
        using var factory = Host();
        var sink = factory.CaptureLogs();
        using var capture = factory.CaptureTelemetry();

        var version = await RequestAsync(factory, sink, capture, SecurityRequests.Version);
        var open = await RequestAsync(factory, sink, capture, IdentityFixtureEndpoint.OpenWhoAmIRoute);

        Assert.Equal(200, version.Status);
        Assert.Equal(200, open.Status);
        AssertNoIdentity(version);
        AssertNoIdentity(open);
    }

    // Breaks when a request rejected with 401 is recorded with an identity: an anonymous caller, a principal without a
    // usable subject, or one without a usable tenant (its user alone must not be recorded either).
    [Theory]
    [InlineData("anonymous", null, null)]
    [InlineData("no subject", "alice", "tenant_id=c0ffee00-0000-4000-8000-000000000002")]
    [InlineData("subject not a guid", "alice", "sub=alice|tenant_id=c0ffee00-0000-4000-8000-000000000002")]
    [InlineData("no tenant", "alice", "sub=c0ffee00-0000-4000-8000-000000000001")]
    [InlineData("tenant not a guid", "alice", "sub=c0ffee00-0000-4000-8000-000000000001|tenant_id=acme")]
    [InlineData("empty tenant", "alice", "sub=c0ffee00-0000-4000-8000-000000000001|tenant_id=00000000-0000-0000-0000-000000000000")]
    public async Task ARequestRejectedWith401CarriesNeitherTheUserNorTheTenant(string description, string? user, string? claims)
    {
        Assert.NotNull(description);
        using var factory = Host();
        var sink = factory.CaptureLogs();
        using var capture = factory.CaptureTelemetry();

        var observed = await RequestAsync(factory, sink, capture, IdentityFixtureEndpoint.WhoAmIRoute, user, claims);

        Assert.Equal(401, observed.Status);
        AssertNoIdentity(observed);
    }

    // Breaks when the identity is only recorded for the requests that succeed: a user the checker refuses (403) is the
    // identity an operator most needs in the log.
    [Fact]
    public async Task AForbiddenRequestCarriesTheUserAndTheTenant()
    {
        using var factory = Host();
        var sink = factory.CaptureLogs();
        using var capture = factory.CaptureTelemetry();

        var observed = await RequestAsync(factory, sink, capture, ProblemFixtureEndpoint.AdminOnly, "alice", IdentityRequests.ClaimsFor(User, Tenant));

        Assert.Equal(403, observed.Status);
        AssertIdentity(observed, User, Tenant);
    }

    // Breaks when the request of another user or tenant is recorded with the identity of the one before it.
    [Fact]
    public async Task EachRequestIsRecordedWithItsOwnIdentity()
    {
        var otherUser = Guid.Parse("c0ffee00-0000-4000-8000-000000000003");
        var otherTenant = Guid.Parse("c0ffee00-0000-4000-8000-000000000004");
        using var factory = Host();
        var sink = factory.CaptureLogs();
        using var capture = factory.CaptureTelemetry();

        var first = await RequestAsync(factory, sink, capture, IdentityFixtureEndpoint.WhoAmIRoute, "alice", IdentityRequests.ClaimsFor(User, Tenant));
        var second = await RequestAsync(factory, sink, capture, IdentityFixtureEndpoint.WhoAmIRoute, "bob", IdentityRequests.ClaimsFor(otherUser, otherTenant));
        var anonymous = await RequestAsync(factory, sink, capture, SecurityRequests.Version);

        AssertIdentity(first, User, Tenant);
        AssertIdentity(second, otherUser, otherTenant);
        AssertNoIdentity(anonymous);
    }

    // Breaks when anything of the principal other than the two identifiers reaches a log event or a span: a name, an email, a
    // token or any other claim value, in the completion event, in any event of the host, or in any attribute of any span.
    [Fact]
    public async Task NoLogEventAndNoSpanHoldsANameAnEmailATokenOrAnotherClaimValueOfThePrincipal()
    {
        var secrets = RequestSecrets.Create();
        using var factory = Host().WithEarlySetting("Logging:LogLevel:Default", "Debug");
        var sink = factory.CaptureLogs();
        using var capture = factory.CaptureTelemetry();
        var claims = TestAuthenticationHandler.ClaimsOf(
            ("sub", User.ToString("D")),
            ("tenant_id", Tenant.ToString("D")),
            ("email", secrets.EmailClaim),
            (ClaimTypes.Email, secrets.EmailClaim),
            ("name", secrets.NameClaim),
            ("given_name", secrets.NameClaim),
            ("access_token", secrets.TokenClaim),
            ("role", secrets.RoleClaim));

        var observed = await RequestAsync(factory, sink, capture, IdentityFixtureEndpoint.WhoAmIRoute, secrets.NameClaim, claims);
        var forbidden = await RequestAsync(factory, sink, capture, ProblemFixtureEndpoint.AdminOnly, secrets.NameClaim, claims);

        AssertIdentity(observed, User, Tenant);
        AssertIdentity(forbidden, User, Tenant);
        foreach (var secret in secrets.IdentityValues)
        {
            foreach (var logEvent in sink.Events)
            {
                Assert.DoesNotContain(
                    logEvent.TextsOf(),
                    text => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
            }

            foreach (var span in capture.Spans)
            {
                Assert.DoesNotContain(span.Texts, text => text.Contains(secret, StringComparison.OrdinalIgnoreCase));
            }
        }

        // The completion event holds the two identifiers and nothing that names a claim: no property of it is called after one.
        Assert.DoesNotContain(
            observed.Completion.Properties.Keys,
            key => key.Contains("email", StringComparison.OrdinalIgnoreCase)
                || key.Contains("name", StringComparison.OrdinalIgnoreCase)
                || key.Contains("token", StringComparison.OrdinalIgnoreCase)
                || key.Contains("claim", StringComparison.OrdinalIgnoreCase)
                || key.Contains("role", StringComparison.OrdinalIgnoreCase));
    }
}
