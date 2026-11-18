using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Hosting;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Contract;

// Behaviour of the document endpoint, seen through ApiHostFactory so the test-only endpoints exercise the rules.
public class ServedDocumentTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private const string Items = "/api/v1/problem-fixtures/items";
    private const string DeclaredErrors = "/api/v1/problem-fixtures/declared-errors";
    private const string WithParameter = "/api/v1/problem-fixtures/count";
    private const string OwnNoInput = "/api/v1/problem-fixtures/own-no-input";
    private const string OwnBadRequest = "/api/v1/problem-fixtures/own-bad-request";

    // Breaks when a problem response is added or left out against the input rule, on any fixture operation.
    [Fact]
    public async Task EveryServedOperationDeclaresTheProblemResponsesItsInputImplies()
    {
        var document = await FetchAsync();

        Assert.Contains(OpenApiContract.Paths(document), p => p.Key == Items);
        Assert.Contains(OpenApiContract.Paths(document), p => p.Key == WithParameter);
        Assert.Contains(OpenApiContract.Paths(document), p => p.Key == DeclaredErrors);

        // The endpoints that declare their own responses are the subject of other tests.
        document["paths"]!.AsObject().Remove(OwnBadRequest);
        document["paths"]!.AsObject().Remove(OwnNoInput);
        OpenApiDocumentRules.AssertCommonProblemResponses(document, OpenApiDocumentRules.ExpectationsOf(factory.Services));
    }

    // Breaks when 408, 413, 415, 429 and 400 are not all declared for an operation with a request body.
    [Fact]
    public async Task OperationWithABodyDeclares400408413415429And500()
    {
        var document = await FetchAsync();

        var operation = OpenApiContract.Paths(document)[Items]!["post"]!.AsObject();

        Assert.NotNull(operation["requestBody"]);
        foreach (var status in new[] { "400", "408", "413", "415", "429", "500" })
        {
            Assert.NotNull(OpenApiContract.ResponseSchema(operation, status, OpenApiContract.ProblemJson));
        }
    }

    // Breaks when an operation with only a parameter gets the body statuses, or loses its 400.
    [Fact]
    public async Task OperationWithOnlyAParameterDeclares400And500ButNoBodyStatuses()
    {
        var document = await FetchAsync();

        var operation = OpenApiContract.Paths(document)[WithParameter]!["get"]!.AsObject();

        Assert.Null(operation["requestBody"]);
        Assert.NotNull(OpenApiContract.ResponseSchema(operation, "400", OpenApiContract.ProblemJson));
        Assert.NotNull(OpenApiContract.ResponseSchema(operation, "500", OpenApiContract.ProblemJson));
        foreach (var status in new[] { "408", "413", "415" })
        {
            Assert.Null(operation["responses"]![status]);
        }
    }

    // Breaks when an operation without input gets a 400 or the body statuses, or loses its 429.
    [Fact]
    public async Task OperationWithoutInputDeclares429And500ButNoInputStatuses()
    {
        var document = await FetchAsync();

        var operation = OpenApiContract.Paths(document)["/api/v1/problem-fixtures/throw"]!["get"]!.AsObject();

        Assert.Null(operation["parameters"]);
        Assert.Null(operation["requestBody"]);
        Assert.NotNull(OpenApiContract.ResponseSchema(operation, "500", OpenApiContract.ProblemJson));
        foreach (var status in new[] { "400", "408", "413", "415" })
        {
            Assert.Null(operation["responses"]![status]);
        }
    }

    // Breaks when the common responses replace or drop what an endpoint declares with ProducesProblem, or add a 400.
    [Fact]
    public async Task DeclaredNotFoundAndConflictAreKeptNextTo500WithoutA400()
    {
        var document = await FetchAsync();

        var responses = OpenApiContract.Paths(document)[DeclaredErrors]!["get"]!["responses"]!.AsObject();

        foreach (var status in new[] { "404", "409", "429", "500" })
        {
            Assert.True(responses.ContainsKey(status), $"Status {status} is missing; found: {string.Join(", ", responses.Select(r => r.Key))}.");
            Assert.NotNull(responses[status]!["content"]?[OpenApiContract.ProblemJson]);
        }

        Assert.False(responses.ContainsKey("400"), "An operation without input must not declare 400.");
    }

    // Breaks when the common 400 overwrites a 400 that the endpoint declares with its own media type and schema.
    [Fact]
    public async Task ResponseDeclaredByTheEndpointIsNotOverwrittenByTheCommonOne()
    {
        var document = await FetchAsync();

        var badRequest = OpenApiContract.Paths(document)[OwnBadRequest]!["post"]!["responses"]!["400"]!.AsObject();

        Assert.NotNull(badRequest["content"]?["application/json"]);
        Assert.Null(badRequest["content"]?[OpenApiContract.ProblemJson]);
    }

    // Breaks when responses an endpoint declares are dropped because the rule says the operation cannot return them.
    [Fact]
    public async Task ResponsesDeclaredByAnEndpointWithoutInputAreKept()
    {
        var document = await FetchAsync();

        var responses = OpenApiContract.Paths(document)[OwnNoInput]!["get"]!["responses"]!.AsObject();

        Assert.NotNull(responses["400"]!["content"]?["application/json"]);
        Assert.Null(responses["400"]!["content"]?[OpenApiContract.ProblemJson]);
        Assert.NotNull(OpenApiContract.ResponseSchema(OpenApiContract.Paths(document)[OwnNoInput]!["get"]!.AsObject(), "413", OpenApiContract.ProblemJson));
        Assert.Null(responses["408"]);
        Assert.Null(responses["415"]);
    }

    // Breaks when the validation schema loses errors or the required problem properties.
    [Fact]
    public async Task ProblemSchemasOfTheServedDocumentIncludingTheValidationOneAreComplete()
    {
        var document = await FetchAsync();

        OpenApiDocumentRules.AssertProblemSchemas(document);
        var validation = OpenApiContract.ResolveSchema(
            document,
            OpenApiContract.ResponseSchema(OpenApiContract.Paths(document)[Items]!["post"]!.AsObject(), "400", OpenApiContract.ProblemJson)!);
        Assert.NotNull(validation["properties"]?["errors"]);
    }

    // Breaks when the document endpoint is not mapped in Development.
    [Fact]
    public async Task DocumentIsServedInDevelopmentAsJson()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("json", response.Content.Headers.ContentType?.MediaType, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))["paths"]);
    }

    // Breaks when the document is exposed in Production, or the 404 is not a problem document.
    [Fact]
    public async Task DocumentAnswers404AsAProblemInProduction()
    {
        using var production = ApiHostFactory.ForEnvironment(Environments.Production);
        using var client = production.CreateClient();

        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(OpenApiContract.ProblemJson, response.Content.Headers.ContentType?.MediaType);
        var problem = OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(404, problem["status"]?.GetValue<int>());
        Assert.False(string.IsNullOrWhiteSpace(problem["code"]?.GetValue<string>()));
    }

    private async Task<JsonObject> FetchAsync()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }
}
