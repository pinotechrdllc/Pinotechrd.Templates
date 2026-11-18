using System.Text.Json.Nodes;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// An operation declares 401 and 403 exactly when its endpoint requires authorization.
public class AuthorizationContractTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private static readonly string[] Statuses = ["401", "403"];

    // Breaks when the served document does not declare 401 and 403 as problem+json for endpoints that require authorization.
    [Theory]
    [InlineData(ProblemFixtureEndpoint.Secured)]
    [InlineData(ProblemFixtureEndpoint.AdminOnly)]
    public async Task OperationThatRequiresAuthorizationDeclares401And403AsProblemJson(string path)
    {
        var document = await FetchAsync();

        var operation = OpenApiContract.Paths(document)[path]!["get"]!.AsObject();

        foreach (var status in Statuses)
        {
            Assert.NotNull(OpenApiContract.ResponseSchema(operation, status, OpenApiContract.ProblemJson));
        }
    }

    // Breaks when 401 or 403 is declared for an operation that is open to anonymous callers.
    [Theory]
    [InlineData(SecurityRequests.Version)]
    [InlineData(ProblemFixtureEndpoint.Unlimited)]
    [InlineData(ProblemFixtureEndpoint.Cached)]
    public async Task OperationThatDoesNotRequireAuthorizationDeclaresNeither401Nor403(string path)
    {
        var document = await FetchAsync();

        var responses = OpenApiContract.Paths(document)[path]!["get"]!["responses"]!.AsObject();

        foreach (var status in Statuses)
        {
            Assert.False(responses.ContainsKey(status), $"{path} declares {status} but does not require authorization.");
        }
    }

    // Breaks when the rule is derived from something else than the endpoint metadata: it must find exactly the two secured fixtures.
    [Fact]
    public void ExpectationsAreReadFromTheEndpointMetadata()
    {
        var expectations = OpenApiDocumentRules.ExpectationsOf(factory.Services);

        Assert.Equal(
            ["GET " + ProblemFixtureEndpoint.AdminOnly, "GET " + ProblemFixtureEndpoint.Secured],
            expectations.Secured.Order(StringComparer.Ordinal));
    }

    // Proves the shared rule can fail: a secured operation without its 401 is rejected.
    [Fact]
    public async Task SharedRuleRejectsASecuredOperationWithout401()
    {
        var document = await FetchAsync();
        document["paths"]![ProblemFixtureEndpoint.Secured]!["get"]!["responses"]!.AsObject().Remove("401");

        Assert.ThrowsAny<Exception>(() => OpenApiDocumentRules.AssertCommonProblemResponses(document, OpenApiDocumentRules.ExpectationsOf(factory.Services)));
    }

    // Proves the shared rule can fail: an open operation that declares 403 is rejected.
    [Fact]
    public async Task SharedRuleRejectsAnOpenOperationThatDeclares403()
    {
        var document = await FetchAsync();
        var responses = document["paths"]![SecurityRequests.Version]!["get"]!["responses"]!.AsObject();
        responses["403"] = document["paths"]![ProblemFixtureEndpoint.Secured]!["get"]!["responses"]!["403"]!.DeepClone();

        Assert.ThrowsAny<Exception>(() => OpenApiDocumentRules.AssertCommonProblemResponses(document, OpenApiDocumentRules.ExpectationsOf(factory.Services)));
    }

    private async Task<JsonObject> FetchAsync()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
