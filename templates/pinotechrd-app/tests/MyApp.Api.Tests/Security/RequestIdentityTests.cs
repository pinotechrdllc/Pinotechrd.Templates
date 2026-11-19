using System.Net;
using System.Security.Claims;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// The user and the tenant of a request come from the claims of its authenticated principal, and only from there: the
// user from `sub`, the tenant from `tenant_id` (both GUIDs, both configurable), set for that request's scope alone.
// A principal whose identity claims are not usable is a 401 on a protected endpoint and is treated as having no identity
// on an anonymous one.
public class RequestIdentityTests
{
    private const string Sub = "sub";

    private const string TenantClaim = "tenant_id";

    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");

    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    private static ApiHostFactory Host() =>
        new ApiHostFactory().WithServices(TestAuthenticationHandler.Register).WithIdentityFixtures();

    private static string Claims(params (string Type, string Value)[] claims) => TestAuthenticationHandler.ClaimsOf(claims);

    // ---- What is set for a valid principal ----

    // Breaks when the user is not read from `sub`, or the tenant from `tenant_id`, or either as another value.
    [Fact]
    public async Task AValidPrincipalSetsTheCurrentUserFromSubAndTheTenantFromTheTenantClaim()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", IdentityRequests.ClaimsFor(UserA, IdentityRequests.TenantA));

        var seen = await response.ReadWhoAmIAsync();
        Assert.True(seen.PrincipalAuthenticated);
        Assert.True(seen.UserAuthenticated);
        Assert.Equal(UserA, seen.UserId);
        Assert.True(seen.TenantSet);
        Assert.Equal(IdentityRequests.TenantA, seen.TenantId);
    }

    // Breaks when the identifiers are read case sensitively or in another text form than the documented GUID form.
    [Fact]
    public async Task AnUpperCaseGuidIsAccepted()
    {
        using var factory = Host();
        using var client = factory.CreateClient();
        var claims = Claims((Sub, UserA.ToString("D").ToUpperInvariant()), (TenantClaim, IdentityRequests.TenantB.ToString("D").ToUpperInvariant()));

        using var response = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", claims);

        var seen = await response.ReadWhoAmIAsync();
        Assert.Equal(UserA, seen.UserId);
        Assert.Equal(IdentityRequests.TenantB, seen.TenantId);
    }

    // Breaks when the user or the tenant of a request is still there for the next one (a static, or a scope that outlives
    // the request): each request shows its own, and an anonymous request after them shows none.
    [Fact]
    public async Task TheUserAndTheTenantAreSetForThatRequestOnly()
    {
        using var factory = Host();
        using var client = factory.CreateClient();

        using var first = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", IdentityRequests.ClaimsFor(UserA, IdentityRequests.TenantA));
        using var second = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "bob", IdentityRequests.ClaimsFor(UserB, IdentityRequests.TenantB));
        using var anonymous = await client.SendAsync(IdentityFixtureEndpoint.OpenWhoAmIRoute);

        var one = await first.ReadWhoAmIAsync();
        var two = await second.ReadWhoAmIAsync();
        var none = await anonymous.ReadWhoAmIAsync();
        Assert.Equal((UserA, (Guid?)IdentityRequests.TenantA), (one.UserId, one.TenantId));
        Assert.Equal((UserB, (Guid?)IdentityRequests.TenantB), (two.UserId, two.TenantId));
        Assert.False(none.PrincipalAuthenticated);
        Assert.False(none.UserAuthenticated);
        Assert.Null(none.UserId);
        Assert.False(none.TenantSet);
        Assert.Null(none.TenantId);
    }

    // Breaks when the tenant or the user of one request leaks into another that runs at the same time (an ambient value
    // shared by the requests): each of 50 concurrent requests, for two tenants and two users, reads back its own tenant
    // after waiting, while the others are in flight.
    [Fact]
    public async Task FiftyConcurrentRequestsOfTwoTenantsNeverSeeEachOthersTenant()
    {
        using var factory = Host();
        using var client = factory.CreateClient();
        var tenants = new[] { IdentityRequests.TenantA, IdentityRequests.TenantB };
        var users = new[] { UserA, UserB };

        var answers = await Task.WhenAll(Enumerable.Range(0, 50).Select(async i =>
        {
            var tenant = tenants[i % 2];
            var user = users[i % 2];
            var claims = IdentityRequests.ClaimsFor(user, tenant);
            using var slow = await client.SendAsync(HttpMethod.Get, IdentityFixtureEndpoint.SlowTenantRoute + $"?delay={5 + (i % 7 * 10)}", "someone", claims);
            using var whoAmI = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "someone", claims);
            return (Expected: tenant, ExpectedUser: user, Slow: await slow.ReadGuidAsync(), Seen: await whoAmI.ReadWhoAmIAsync());
        }));

        Assert.Equal(50, answers.Length);
        Assert.All(answers, a =>
        {
            Assert.Equal(a.Expected, a.Slow);
            Assert.Equal(a.Expected, a.Seen.TenantId);
            Assert.Equal(a.ExpectedUser, a.Seen.UserId);
        });
    }

    // Breaks when the tenant is taken from anything but the token: a header, the query string or the body that names another
    // tenant is ignored, for the tenant and for the user.
    [Fact]
    public async Task ATenantInAHeaderTheQueryStringOrTheBodyIsIgnored()
    {
        using var factory = Host();
        using var client = factory.CreateClient();
        var other = IdentityRequests.TenantB;
        var headers = new Dictionary<string, string>
        {
            ["X-Tenant-Id"] = other.ToString("D"),
            ["X-Tenant"] = other.ToString("D"),
            ["Tenant-Id"] = other.ToString("D"),
            ["tenant_id"] = other.ToString("D"),
            ["X-User-Id"] = UserB.ToString("D"),
        };
        var query = $"?tenant_id={other}&tenantId={other}&tenant={other}&sub={UserB}";
        var claims = IdentityRequests.ClaimsFor(UserA, IdentityRequests.TenantA);

        using var byHeaderAndQuery = await client.SendAsync(HttpMethod.Get, IdentityFixtureEndpoint.WhoAmIRoute + query, "alice", claims, headers);
        using var byBody = await client.SendAsync(
            HttpMethod.Post, IdentityFixtureEndpoint.WhoAmIRoute, "alice", claims, content: IdentityRequests.BodyNamingTenant(other));

        foreach (var response in new[] { byHeaderAndQuery, byBody })
        {
            var seen = await response.ReadWhoAmIAsync();
            Assert.Equal(IdentityRequests.TenantA, seen.TenantId);
            Assert.Equal(UserA, seen.UserId);
        }
    }

    // Breaks when an anonymous request gets a tenant from a header, the query string or the body, or a principal with no
    // tenant claim falls back to one of them.
    [Fact]
    public async Task ATenantNamedByTheRequestIsNotAFallbackForARequestWithoutATenantClaim()
    {
        using var factory = Host();
        using var client = factory.CreateClient();
        var headers = new Dictionary<string, string> { ["X-Tenant-Id"] = IdentityRequests.TenantB.ToString("D") };
        var query = $"?tenant_id={IdentityRequests.TenantB}";

        using var anonymous = await client.SendAsync(HttpMethod.Get, IdentityFixtureEndpoint.OpenWhoAmIRoute + query, headers: headers);
        using var noTenantClaim = await client.SendAsync(
            HttpMethod.Get, IdentityFixtureEndpoint.WhoAmIRoute + query, "alice", Claims((Sub, UserA.ToString("D"))), headers);

        var seen = await anonymous.ReadWhoAmIAsync();
        Assert.False(seen.TenantSet);
        Assert.Null(seen.TenantId);
        await ProblemAssert.IsProblemAsync(noTenantClaim, 401, "unauthorized");
    }

    // ---- A principal whose tenant claim is not usable ----

    public static TheoryData<string, string[]> UnusableTenantClaims
    {
        get
        {
            var valid = IdentityRequests.TenantA.ToString("D");
            return new TheoryData<string, string[]>
            {
                { "missing", [] },
                { "not a guid", ["not-a-guid"] },
                { "empty text", [""] },
                { "white space", ["   "] },
                { "empty guid", [Guid.Empty.ToString("D")] },
                { "two claims, different", [valid, IdentityRequests.TenantB.ToString("D")] },
                { "two claims, the same value", [valid, valid] },
                { "guid without hyphens", [IdentityRequests.TenantA.ToString("N")] },
                { "guid in braces", [IdentityRequests.TenantA.ToString("B")] },
                { "guid with trailing text", [valid + "x"] },
                { "guid with a leading space", [" " + valid] },
                { "guid with a trailing space", [valid + " "] },
                { "guid in parentheses", [IdentityRequests.TenantA.ToString("P")] },
            };
        }
    }

    private static string WithTenantClaims(string[] tenantValues) =>
        Claims([.. tenantValues.Select(v => (TenantClaim, v)), (Sub, UserA.ToString("D"))]);

    // Breaks when a principal without one valid tenant claim reaches a protected endpoint (a tenant would be absent or
    // ambiguous inside the business code), or is answered with something else than the 401 unauthorized problem.
    [Theory]
    [MemberData(nameof(UnusableTenantClaims))]
    public async Task APrincipalWithoutOneValidTenantClaimAnswers401OnAProtectedEndpoint(string description, string[] tenantValues)
    {
        Assert.NotNull(description);
        var checker = new TestPermissionChecker(ProblemFixtureEndpoint.AdminPermission);
        using var factory = Host().WithPermissionChecker(checker);
        using var client = factory.CreateClient();
        var claims = WithTenantClaims(tenantValues);

        using var authenticatedOnly = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", claims);
        using var permission = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice", claims);

        await ProblemAssert.IsProblemAsync(authenticatedOnly, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(permission, 401, "unauthorized");
        Assert.Empty(checker.Calls);
    }

    // Breaks when the endpoint that allows anonymous callers is closed to a logged-in principal with a bad tenant claim, or
    // when it is given a tenant anyway. The principal is still authenticated, and still has its user.
    [Theory]
    [MemberData(nameof(UnusableTenantClaims))]
    public async Task APrincipalWithoutOneValidTenantClaimCanStillCallAnAnonymousEndpoint(string description, string[] tenantValues)
    {
        Assert.NotNull(description);
        using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(IdentityFixtureEndpoint.OpenWhoAmIRoute, "alice", WithTenantClaims(tenantValues));

        var seen = await response.ReadWhoAmIAsync();
        Assert.True(seen.PrincipalAuthenticated);
        Assert.False(seen.TenantSet);
        Assert.Null(seen.TenantId);
    }

    // ---- A principal whose subject is not usable ----

    public static TheoryData<string, string[]> UnusableSubjects
    {
        get
        {
            var valid = UserA.ToString("D");
            return new TheoryData<string, string[]>
            {
                { "missing", [] },
                { "not a guid", ["alice"] },
                { "empty text", [""] },
                { "empty guid", [Guid.Empty.ToString("D")] },
                { "two claims, different", [valid, UserB.ToString("D")] },
                { "two claims, the same value", [valid, valid] },
                { "guid without hyphens", [UserA.ToString("N")] },
                { "guid with a leading space", [" " + valid] },
                { "guid with a trailing space", [valid + " "] },
                { "guid in braces", [UserA.ToString("B")] },
            };
        }
    }

    private static string WithSubjects(string[] subjects) =>
        Claims([.. subjects.Select(v => (Sub, v)), (TenantClaim, IdentityRequests.TenantA.ToString("D"))]);

    // Breaks when a principal without one valid `sub` reaches a protected endpoint, where no user could be named in the
    // audit or asked of the permission checker.
    [Theory]
    [MemberData(nameof(UnusableSubjects))]
    public async Task APrincipalWithoutAValidSubjectAnswers401OnProtectedEndpoints(string description, string[] subjects)
    {
        Assert.NotNull(description);
        var checker = new TestPermissionChecker(ProblemFixtureEndpoint.AdminPermission);
        using var factory = Host().WithPermissionChecker(checker);
        using var client = factory.CreateClient();
        var claims = WithSubjects(subjects);

        using var authenticatedOnly = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", claims);
        using var permission = await client.GetAsAsync(ProblemFixtureEndpoint.AdminOnly, "alice", claims);

        await ProblemAssert.IsProblemAsync(authenticatedOnly, 401, "unauthorized");
        await ProblemAssert.IsProblemAsync(permission, 401, "unauthorized");
        Assert.Empty(checker.Calls);
    }

    // Breaks when the user of an anonymous endpoint is set from a subject that is not usable: it has no user then.
    [Theory]
    [MemberData(nameof(UnusableSubjects))]
    public async Task APrincipalWithoutAValidSubjectHasNoCurrentUserOnAnAnonymousEndpoint(string description, string[] subjects)
    {
        Assert.NotNull(description);
        using var factory = Host();
        using var client = factory.CreateClient();

        using var response = await client.GetAsAsync(IdentityFixtureEndpoint.OpenWhoAmIRoute, "alice", WithSubjects(subjects));

        var seen = await response.ReadWhoAmIAsync();
        Assert.True(seen.PrincipalAuthenticated);
        Assert.False(seen.UserAuthenticated);
        Assert.Null(seen.UserId);
        // Without a user there is no tenant either, although the principal's tenant claim is valid.
        Assert.False(seen.TenantSet);
        Assert.Null(seen.TenantId);
    }

    // ---- Other claims are not identity ----

    // Breaks when the user is read from a claim other than `sub` (the name, the long name-identifier URI) while the settings
    // keep the default: a principal that only has those has no usable subject.
    [Theory]
    [InlineData(ClaimTypes.NameIdentifier)]
    [InlineData("nameid")]
    [InlineData("uid")]
    [InlineData("user_id")]
    [InlineData("id")]
    [InlineData("SUB")]
    [InlineData("Sub")]
    public async Task OnlyTheConfiguredClaimTypeNamesTheUser(string otherType)
    {
        using var factory = Host();
        using var client = factory.CreateClient();
        var claims = Claims((otherType, UserA.ToString("D")), (TenantClaim, IdentityRequests.TenantA.ToString("D")));

        using var response = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", claims);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // Breaks when a tenant is read from a claim other than the configured one.
    [Theory]
    [InlineData("tid")]
    [InlineData("tenant")]
    [InlineData("tenantid")]
    [InlineData("TENANT_ID")]
    [InlineData("Tenant_Id")]
    public async Task OnlyTheConfiguredClaimTypeNamesTheTenant(string otherType)
    {
        using var factory = Host();
        using var client = factory.CreateClient();
        var claims = Claims((Sub, UserA.ToString("D")), (otherType, IdentityRequests.TenantA.ToString("D")));

        using var response = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", claims);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // ---- Custom claim types (the Identity section) ----

    private static ApiHostFactory CustomClaimTypes(string userType, string tenantType) =>
        Host().WithEarlySetting("Identity:UserIdClaimType", userType).WithEarlySetting("Identity:TenantIdClaimType", tenantType);

    // Breaks when the claim types of the settings are not used: the identity is then read from `sub` and `tenant_id`.
    [Theory]
    [InlineData("uid", "org")]
    [InlineData(ClaimTypes.NameIdentifier, "http://schemas.example.com/claims/tenant")]
    public async Task TheClaimTypesOfTheIdentitySettingsReplaceTheDefaults(string userType, string tenantType)
    {
        using var factory = CustomClaimTypes(userType, tenantType);
        using var client = factory.CreateClient();
        var custom = Claims((userType, UserA.ToString("D")), (tenantType, IdentityRequests.TenantA.ToString("D")));
        var defaults = IdentityRequests.ClaimsFor(UserB, IdentityRequests.TenantB);

        using var accepted = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", custom);
        using var defaultsOnly = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", defaults);

        var seen = await accepted.ReadWhoAmIAsync();
        Assert.Equal(UserA, seen.UserId);
        Assert.Equal(IdentityRequests.TenantA, seen.TenantId);
        await ProblemAssert.IsProblemAsync(defaultsOnly, 401, "unauthorized");
    }

    // Breaks when the rules for an unusable claim are only applied to the default claim types.
    [Theory]
    [InlineData("missing user", "", "tenant")]
    [InlineData("user not a guid", "alice", "tenant")]
    [InlineData("user is the empty guid", "00000000-0000-0000-0000-000000000000", "tenant")]
    [InlineData("tenant missing", "user", "")]
    [InlineData("tenant not a guid", "user", "not-a-guid")]
    [InlineData("tenant is the empty guid", "user", "00000000-0000-0000-0000-000000000000")]
    public async Task UnusableClaimsOfCustomTypesAnswer401OnProtectedEndpointsAndAreIgnoredOnAnonymousOnes(string description, string userValue, string tenantValue)
    {
        Assert.NotNull(description);
        using var factory = CustomClaimTypes("uid", "org");
        using var client = factory.CreateClient();
        var user = userValue == "user" ? UserA.ToString("D") : userValue;
        var tenant = tenantValue == "tenant" ? IdentityRequests.TenantA.ToString("D") : tenantValue;
        var list = new List<(string Type, string Value)>();
        if (user.Length > 0)
        {
            list.Add(("uid", user));
        }

        if (tenant.Length > 0)
        {
            list.Add(("org", tenant));
        }

        var claims = Claims([.. list]);

        using var protectedEndpoint = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", claims);
        using var anonymousEndpoint = await client.GetAsAsync(IdentityFixtureEndpoint.OpenWhoAmIRoute, "alice", claims);

        await ProblemAssert.IsProblemAsync(protectedEndpoint, 401, "unauthorized");
        Assert.Equal(HttpStatusCode.OK, anonymousEndpoint.StatusCode);
    }

    // Breaks when the claim types of the settings are compared ignoring case.
    [Theory]
    [InlineData("UID", "org")]
    [InlineData("uid", "ORG")]
    public async Task TheClaimTypesOfTheIdentitySettingsAreComparedExactly(string userType, string tenantType)
    {
        using var factory = CustomClaimTypes("uid", "org");
        using var client = factory.CreateClient();
        var claims = Claims((userType, UserA.ToString("D")), (tenantType, IdentityRequests.TenantA.ToString("D")));

        using var response = await client.GetAsAsync(IdentityFixtureEndpoint.WhoAmIRoute, "alice", claims);

        await ProblemAssert.IsProblemAsync(response, 401, "unauthorized");
    }

    // Breaks when a blank claim type is accepted, which would make every authenticated request a 401 with no explanation:
    // the host does not start, and the message names the setting.
    [Theory]
    [InlineData("Identity:UserIdClaimType", "UserIdClaimType", "")]
    [InlineData("Identity:UserIdClaimType", "UserIdClaimType", "   ")]
    [InlineData("Identity:UserIdClaimType", "UserIdClaimType", "su b")]
    [InlineData("Identity:TenantIdClaimType", "TenantIdClaimType", "")]
    [InlineData("Identity:TenantIdClaimType", "TenantIdClaimType", "   ")]
    [InlineData("Identity:TenantIdClaimType", "TenantIdClaimType", "tenant id")]
    public void ABlankClaimTypeSettingStopsTheStart(string key, string setting, string value)
    {
        using var factory = new ApiHostFactory().WithSetting(key, value);

        StartupFailure.AssertOptionsInvalid(factory, setting);
    }
}
