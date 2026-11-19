using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Fixtures;

/// <summary>
/// What a developer's first endpoint in a module must get right, through the real API host: where it is served, what it
/// answers to a good and to a bad request, and how it appears in the served OpenAPI document.
/// </summary>
public class CreateWidgetEndpointTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private static readonly string ModuleSegment = ModuleRegistration.RouteSegmentOf("MyModule");
    private static readonly string WidgetsPath = $"/api/v1/{ModuleSegment}/widgets";

    // Breaks when the endpoint stops answering 200, stops echoing the values, or writes them with other property names.
    [Fact]
    public async Task AValidBodyAnswers200AndEchoesTheValuesWithCamelCaseNames()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(WidgetsPath, new { name = "gear", quantity = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal(["name", "quantity"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("gear", body["name"]!.GetValue<string>());
        Assert.Equal(3, body["quantity"]!.GetValue<int>());
    }

    // Breaks when the module's request body is not validated (for example AddValidation() is missing from the module), or
    // when a field is not reported under its JSON name.
    [Fact]
    public async Task AnInvalidBodyAnswers400ValidationFailedWithAnErrorForEachInvalidField()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(WidgetsPath, new { name = "", quantity = 99 }, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.IsProblemAsync(response, 400, "validation_failed");
        var errors = problem.GetProperty("errors");
        Assert.Equal(["name", "quantity"], errors.EnumerateObject().Select(e => e.Name).Order(StringComparer.Ordinal));
        Assert.All(errors.EnumerateObject(), e => Assert.NotEmpty(e.Value.EnumerateArray()));
    }

    // Breaks when a valid field is reported together with the invalid one.
    [Theory]
    [InlineData("", 5, "name")]
    [InlineData("gear", 0, "quantity")]
    [InlineData("gear", 11, "quantity")]
    public async Task ABodyWithOneInvalidFieldReportsOnlyThatField(string name, int quantity, string field)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(WidgetsPath, new { name, quantity }, TestContext.Current.CancellationToken);

        var problem = await ProblemAssert.IsProblemAsync(response, 400, "validation_failed");
        Assert.Equal([field], problem.GetProperty("errors").EnumerateObject().Select(e => e.Name));
    }

    // Breaks when the ends of the documented range (1 to 10) are refused.
    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public async Task TheLimitsOfTheQuantityRangeAreAccepted(int quantity)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(WidgetsPath, new { name = "gear", quantity }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks when malformed JSON is answered with another status, another code or a body that is not a problem document.
    [Fact]
    public async Task MalformedJsonAnswers400InvalidJson()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("{ \"name\": ", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync(WidgetsPath, content, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 400, "invalid_json");
    }

    // Breaks when the endpoint is also served without the module segment or at the root.
    [Theory]
    [InlineData("/api/v1/widgets")]
    [InlineData("/widgets")]
    public async Task TheEndpointIsNotServedWithoutTheModulePrefix(string path)
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(path, new { name = "gear", quantity = 3 }, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 404, "not_found");
    }

    // Breaks when a verb the endpoint does not map is answered with another status or code.
    [Fact]
    public async Task TheWrongMethodAnswers405MethodNotAllowed()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(WidgetsPath, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 405, "method_not_allowed");
    }

    // Breaks when the operation moves out of the module prefix, is also listed elsewhere, or loses its name.
    [Fact]
    public async Task TheServedDocumentHasTheOperationAtTheModulePathWithItsOperationIdAndNowhereElse()
    {
        var document = await FetchDocumentAsync();

        var operation = OpenApiContract.Paths(document)[WidgetsPath]!["post"]!.AsObject();

        Assert.Equal("CreateWidget", operation["operationId"]!.GetValue<string>());
        Assert.Equal(
            [WidgetsPath],
            OpenApiContract.Paths(document).Select(p => p.Key).Where(p => p.EndsWith("/widgets", StringComparison.Ordinal)));
    }

    // Breaks when the operation is not grouped under the module name, or keeps the default tag next to it.
    [Fact]
    public async Task TheOperationIsTaggedWithTheModuleNameAlone()
    {
        var document = await FetchDocumentAsync();

        var operation = OpenApiContract.Paths(document)[WidgetsPath]!["post"]!.AsObject();

        Assert.Equal(["MyModule"], OpenApiContract.Tags(operation));
    }

    // Breaks when the request body is not described, or its members are not named as they appear in the JSON.
    [Fact]
    public async Task TheRequestBodySchemaDescribesNameAndQuantity()
    {
        var document = await FetchDocumentAsync();

        var operation = OpenApiContract.Paths(document)[WidgetsPath]!["post"]!.AsObject();
        var schema = OpenApiContract.ResolveSchema(document, operation["requestBody"]!["content"]!["application/json"]!["schema"]!.AsObject());

        var properties = schema["properties"]!.AsObject();
        Assert.Equal(["name", "quantity"], properties.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    // Breaks when the operation loses a problem response the contract rules require of an operation with a body, or
    // declares one that it cannot return (it is anonymous, so no 401 and no 403, and it does not opt out of rate limiting).
    [Fact]
    public async Task TheOperationDeclaresTheProblemResponsesTheContractRulesRequire()
    {
        var document = await FetchDocumentAsync();

        var operation = OpenApiContract.Paths(document)[WidgetsPath]!["post"]!.AsObject();

        foreach (var status in new[] { "400", "408", "413", "415", "429", "500" })
        {
            Assert.True(
                OpenApiContract.ResponseSchema(operation, status, OpenApiContract.ProblemJson) is not null,
                $"POST {WidgetsPath} does not declare {status} as {OpenApiContract.ProblemJson}.");
        }

        foreach (var status in new[] { "401", "403" })
        {
            Assert.Null(operation["responses"]![status]);
        }
    }

    // Breaks when the operation is documented as needing a login: the fixture endpoint is AllowAnonymous(), so it declares no
    // security requirement (an anonymous operation references no scheme).
    [Fact]
    public async Task TheOperationDeclaresNoSecurityRequirementBecauseTheEndpointIsAnonymous()
    {
        var document = await FetchDocumentAsync();

        var operation = OpenApiContract.Paths(document)[WidgetsPath]!["post"]!.AsObject();

        Assert.True(operation["security"] is null or System.Text.Json.Nodes.JsonArray { Count: 0 }, $"POST {WidgetsPath} declares a security requirement but is anonymous.");
    }

    // Breaks when the anonymous endpoint needs credentials: it answers an anonymous caller with 200, not 401.
    [Fact]
    public async Task TheEndpointAnswersAnAnonymousCallerBecauseItIsAnonymous()
    {
        using var client = factory.CreateClient();

        using var response = await client.PostAsJsonAsync(WidgetsPath, new { name = "gear", quantity = 3 }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(response.Headers.WwwAuthenticate);
    }

    // Breaks when the common rules of the contract no longer hold for the document that includes the module endpoint.
    [Fact]
    public async Task TheServedDocumentWithTheModuleEndpointSatisfiesTheCommonContractRules()
    {
        var document = await FetchDocumentAsync();

        // Only the module endpoint is the subject: the test-only endpoints of the host declare their own responses on
        // purpose and are covered by the contract tests.
        var paths = OpenApiContract.Paths(document);
        foreach (var path in paths.Select(p => p.Key).Where(p => p != WidgetsPath).ToList())
        {
            paths.Remove(path);
        }

        OpenApiDocumentRules.AssertCommonProblemResponses(document, OpenApiDocumentRules.ExpectationsOf(factory.Services));
        OpenApiDocumentRules.AssertTags(document);
        OpenApiDocumentRules.AssertProblemSchemas(document);
    }

    private async Task<JsonObject> FetchDocumentAsync()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
