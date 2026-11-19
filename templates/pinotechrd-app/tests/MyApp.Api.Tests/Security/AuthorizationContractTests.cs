using System.Text.Json.Nodes;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// An operation is one of three kinds, by how its endpoint declares access:
//   anonymous      (AllowAnonymous):     no security requirement, no 401, no 403 (the sign-in endpoints declare their own);
//   authenticated  (AuthenticatedOnly):  the Bearer requirement and 401, no 403;
//   permission     (RequirePermission):  the Bearer requirement, 401 and 403.
public class AuthorizationContractTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private const string Anonymous = SecurityRequests.Version;

    // Breaks when the served document does not declare the Bearer scheme as an http bearer scheme.
    [Fact]
    public async Task TheDocumentDeclaresTheBearerSchemeAsHttpBearer()
    {
        var document = await FetchAsync();

        var scheme = document["components"]!["securitySchemes"]!["Bearer"]!.AsObject();

        Assert.Equal("http", scheme["type"]!.GetValue<string>());
        Assert.Equal("bearer", scheme["scheme"]!.GetValue<string>(), ignoreCase: true);
        Assert.Null(document["security"]);
    }

    // Breaks when an authenticated operation loses its 401 as problem+json, its Bearer requirement, or declares a 403 it
    // cannot return (any authenticated user may call it).
    [Fact]
    public async Task AnAuthenticatedOperationDeclaresBearerAnd401AsProblemJsonAndNo403()
    {
        var document = await FetchAsync();

        var operation = OpenApiContract.Paths(document)[ProblemFixtureEndpoint.Secured]!["get"]!.AsObject();

        Assert.NotNull(OpenApiContract.ResponseSchema(operation, "401", OpenApiContract.ProblemJson));
        Assert.Null(operation["responses"]!["403"]);
        AssertBearerRequirement(operation);
    }

    // Breaks when an operation that requires a permission does not declare 401 and 403 as problem+json, or the Bearer requirement.
    [Fact]
    public async Task APermissionOperationDeclaresBearerAnd401And403AsProblemJson()
    {
        var document = await FetchAsync();

        var operation = OpenApiContract.Paths(document)[ProblemFixtureEndpoint.AdminOnly]!["get"]!.AsObject();

        Assert.NotNull(OpenApiContract.ResponseSchema(operation, "401", OpenApiContract.ProblemJson));
        Assert.NotNull(OpenApiContract.ResponseSchema(operation, "403", OpenApiContract.ProblemJson));
        AssertBearerRequirement(operation);
    }

    // Breaks when 401, 403 or a security requirement is declared for an operation that is open to anonymous callers.
    [Theory]
    [InlineData(Anonymous)]
    [InlineData(ProblemFixtureEndpoint.Unlimited)]
    [InlineData(ProblemFixtureEndpoint.Cached)]
    public async Task AnAnonymousOperationDeclaresNeitherSecurityNor401Nor403(string path)
    {
        var document = await FetchAsync();

        var operation = OpenApiContract.Paths(document)[path]!["get"]!.AsObject();

        Assert.False(operation["responses"]!.AsObject().ContainsKey("401"), $"{path} declares 401 but is anonymous.");
        Assert.False(operation["responses"]!.AsObject().ContainsKey("403"), $"{path} declares 403 but is anonymous.");
        Assert.True(operation["security"] is null or JsonArray { Count: 0 }, $"{path} declares a security requirement but is anonymous.");
    }

    // Breaks when the rule is derived from something else than the declared access: it must find the two protected
    // fixtures, and the permission one among them.
    [Fact]
    public void ExpectationsAreReadFromTheAccessDeclaredOnTheEndpoints()
    {
        var expectations = OpenApiDocumentRules.ExpectationsOf(factory.Services);

        Assert.Equal(
            ["GET " + ProblemFixtureEndpoint.AdminOnly, "GET " + ProblemFixtureEndpoint.Secured, "POST " + AuthRequests.ChangePassword],
            expectations.Authenticated.Order(StringComparer.Ordinal));
        Assert.Equal(["GET " + ProblemFixtureEndpoint.AdminOnly], expectations.Permission);
    }

    // Breaks when the sign-in, refresh and logout endpoints stop declaring the 401 and 403 their own answers include, or the
    // rule starts reading the declaration from something else (the change of password is authenticated, not anonymous, and implies no 403).
    [Fact]
    public void TheAnonymousAuthEndpointsDeclareTheirOwn401And403()
    {
        var expectations = OpenApiDocumentRules.ExpectationsOf(factory.Services);

        Assert.True(expectations.DeclaresItself("POST " + AuthRequests.Login, "401"));
        Assert.True(expectations.DeclaresItself("POST " + AuthRequests.Login, "403"));
        Assert.True(expectations.DeclaresItself("POST " + AuthRequests.Refresh, "401"));
        Assert.True(expectations.DeclaresItself("POST " + AuthRequests.Refresh, "403"));
        Assert.True(expectations.DeclaresItself("POST " + AuthRequests.Logout, "403"));
        Assert.False(expectations.DeclaresItself("POST " + AuthRequests.Logout, "401"));
        Assert.False(expectations.DeclaresItself("GET " + Anonymous, "401"));
    }

    // Proves the shared rule can fail: an anonymous endpoint that declares 401 itself must document it (the rule is not simply off for it).
    [Fact]
    public async Task SharedRuleRejectsADeclaredAnonymousOperationWithoutItsOwn401()
    {
        var document = await FetchAsync();
        PostOperationOf(document, AuthRequests.Login)["responses"]!.AsObject().Remove("401");

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: an anonymous endpoint that does not declare 401 (the logout) is rejected when the document has one.
    [Fact]
    public async Task SharedRuleRejectsAnAnonymousOperationThatDeclaresAnother401ItsEndpointDoesNot()
    {
        var document = await FetchAsync();
        var responses = PostOperationOf(document, AuthRequests.Logout)["responses"]!.AsObject();
        responses["401"] = PostOperationOf(document, AuthRequests.Login)["responses"]!["401"]!.DeepClone();

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: a sign-in endpoint that gains the Bearer requirement is rejected, declared statuses or not.
    [Fact]
    public async Task SharedRuleRejectsADeclaredAnonymousOperationThatReferencesTheBearerScheme()
    {
        var document = await FetchAsync();
        PostOperationOf(document, AuthRequests.Login)["security"] = OperationOf(document, ProblemFixtureEndpoint.Secured)["security"]!.DeepClone();

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: an authenticated operation without its 401 is rejected.
    [Fact]
    public async Task SharedRuleRejectsAnAuthenticatedOperationWithout401()
    {
        var document = await FetchAsync();
        OperationOf(document, ProblemFixtureEndpoint.Secured)["responses"]!.AsObject().Remove("401");

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: an authenticated operation that declares a 403 (only a permission has one) is rejected.
    [Fact]
    public async Task SharedRuleRejectsAnAuthenticatedOperationThatDeclares403()
    {
        var document = await FetchAsync();
        var responses = OperationOf(document, ProblemFixtureEndpoint.Secured)["responses"]!.AsObject();
        responses["403"] = OperationOf(document, ProblemFixtureEndpoint.AdminOnly)["responses"]!["403"]!.DeepClone();

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: a permission operation without its 403 is rejected.
    [Fact]
    public async Task SharedRuleRejectsAPermissionOperationWithout403()
    {
        var document = await FetchAsync();
        OperationOf(document, ProblemFixtureEndpoint.AdminOnly)["responses"]!.AsObject().Remove("403");

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: an anonymous operation that declares 403 is rejected.
    [Fact]
    public async Task SharedRuleRejectsAnAnonymousOperationThatDeclares403()
    {
        var document = await FetchAsync();
        var responses = OperationOf(document, Anonymous)["responses"]!.AsObject();
        responses["403"] = OperationOf(document, ProblemFixtureEndpoint.AdminOnly)["responses"]!["403"]!.DeepClone();

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: an anonymous operation that declares a 401 is rejected.
    [Fact]
    public async Task SharedRuleRejectsAnAnonymousOperationThatDeclares401()
    {
        var document = await FetchAsync();
        var responses = OperationOf(document, Anonymous)["responses"]!.AsObject();
        responses["401"] = OperationOf(document, ProblemFixtureEndpoint.Secured)["responses"]!["401"]!.DeepClone();

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: an authenticated operation without its security requirement is rejected.
    [Fact]
    public async Task SharedRuleRejectsAnAuthenticatedOperationWithoutTheBearerRequirement()
    {
        var document = await FetchAsync();
        OperationOf(document, ProblemFixtureEndpoint.Secured).Remove("security");

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: an anonymous operation that references the Bearer scheme is rejected.
    [Fact]
    public async Task SharedRuleRejectsAnAnonymousOperationThatReferencesTheBearerScheme()
    {
        var document = await FetchAsync();
        OperationOf(document, Anonymous)["security"] = OperationOf(document, ProblemFixtureEndpoint.Secured)["security"]!.DeepClone();

        AssertRuleRejects(document);
    }

    // Proves the shared rule can fail: a document without the Bearer scheme is rejected.
    [Fact]
    public async Task SharedRuleRejectsADocumentWithoutTheBearerScheme()
    {
        var document = await FetchAsync();
        document["components"]!["securitySchemes"]!.AsObject().Remove("Bearer");

        AssertRuleRejects(document);
    }

    private static void AssertBearerRequirement(JsonObject operation)
    {
        var requirements = operation["security"]!.AsArray();
        var requirement = Assert.Single(requirements)!.AsObject();
        Assert.Equal(["Bearer"], requirement.Select(p => p.Key));
        Assert.Empty(requirement["Bearer"]!.AsArray());
    }

    private static JsonObject PostOperationOf(JsonObject document, string path) => document["paths"]![path]!["post"]!.AsObject();

    private static JsonObject OperationOf(JsonObject document, string path) => document["paths"]![path]!["get"]!.AsObject();

    private void AssertRuleRejects(JsonObject document) =>
        Assert.ThrowsAny<Exception>(() => OpenApiDocumentRules.AssertCommonProblemResponses(document, OpenApiDocumentRules.ExpectationsOf(factory.Services)));

    private async Task<JsonObject> FetchAsync()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
