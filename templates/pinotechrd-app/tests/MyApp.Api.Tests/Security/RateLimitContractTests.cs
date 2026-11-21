using System.Text.Json.Nodes;
using MyApp.Api.Tests.Problems;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Security;

// The catalogue and the OpenAPI documents describe the 429 answer.
public class RateLimitContractTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    // Breaks when rate_limited is missing from the catalogue or listed with another status.
    [Fact]
    public void CatalogueListsRateLimitedWith429()
    {
        var row = ProblemCodeCatalogueTests.ReadCatalogue().Single(r => r.Code == "rate_limited");

        Assert.Equal("429", row.Status);
        Assert.Contains("Retry-After", row.Meaning, StringComparison.Ordinal);
    }

    // Breaks when an operation of a committed contract stops declaring 429 as problem+json. In the template source it
    // checks the file of each architecture.
    [Fact]
    public void CommittedContractDeclares429AsProblemJsonOnEveryApiOperation()
    {
        foreach (var path in CommittedPaths())
        {
            AssertEveryApiOperationDeclares429(OpenApiContract.Read(path), Path.GetFileName(path));
        }
    }

    // Breaks when the served document stops declaring 429 as problem+json on the real and the fixture operations.
    [Fact]
    public async Task ServedDocumentDeclares429AsProblemJsonOnEveryApiOperation()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var document = OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        AssertEveryApiOperationDeclares429(document, "The served document");
        Assert.Contains(OpenApiContract.Paths(document), p => p.Key.StartsWith(ProblemFixtureEndpoint.Base, StringComparison.Ordinal));
    }

    // Breaks when an operation marked with DisableRateLimiting() still declares a 429 it can never return, or when the
    // exemption widens: the only operation without 429 in the served document is the exempt one.
    [Fact]
    public async Task OnlyTheOperationMarkedWithDisableRateLimitingOmits429()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        var document = OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

        var without = OpenApiContract.Operations(document)
            .Where(o => o.Operation["responses"]?["429"] is null)
            .Select(o => o.Path)
            .ToArray();

        Assert.Equal([ProblemFixtureEndpoint.Unlimited], without);
        Assert.Contains("GET " + ProblemFixtureEndpoint.Unlimited, Expectations().Unlimited);
    }

    // Proves the shared rule can fail: a document where one operation lost its 429 must be rejected.
    [Fact]
    public void SharedRuleRejectsAnOperationWithoutA429()
    {
        foreach (var path in CommittedPaths())
        {
            var document = OpenApiContract.Read(path);
            var (_, _, operation) = OpenApiContract.Operations(document).First();
            operation["responses"]!.AsObject().Remove("429");

            Assert.ThrowsAny<Exception>(() => OpenApiDocumentRules.AssertCommonProblemResponses(document, Expectations()));
        }
    }

    // Proves the shared rule can fail: a 429 declared with another media type does not count.
    [Fact]
    public void SharedRuleRejectsA429ThatIsNotProblemJson()
    {
        foreach (var path in CommittedPaths())
        {
            var document = OpenApiContract.Read(path);
            var (_, _, operation) = OpenApiContract.Operations(document).First();
            var content = operation["responses"]!["429"]!["content"]!.AsObject();
            var schema = content[OpenApiContract.ProblemJson]!.DeepClone();
            content.Remove(OpenApiContract.ProblemJson);
            content["application/json"] = schema;

            Assert.ThrowsAny<Exception>(() => OpenApiDocumentRules.AssertCommonProblemResponses(document, Expectations()));
        }
    }

    /// <summary>Every committed contract file: one in a generated project, one per architecture in the template source.</summary>
    private static IReadOnlyList<string> CommittedPaths()
    {
        var paths = OpenApiContract.ContractPaths;
        Assert.NotEmpty(paths);
        return paths;
    }

    private OpenApiDocumentRules.EndpointExpectations Expectations() => OpenApiDocumentRules.ExpectationsOf(factory.Services);

    private void AssertEveryApiOperationDeclares429(JsonObject document, string name)
    {
        var operations = OpenApiContract.Operations(document)
            .Where(o => o.Path.StartsWith(MyApp.BuildingBlocks.Web.EndpointRegistration.RoutePrefix, StringComparison.Ordinal))
            .Where(o => !Expectations().Unlimited.Contains($"{o.Method.ToUpperInvariant()} {o.Path}"))
            .ToArray();
        Assert.True(operations.Length > 0, $"{name} has no API operations.");

        var missing = operations
            .Where(o => OpenApiContract.ResponseSchema(o.Operation, "429", OpenApiContract.ProblemJson) is null)
            .Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}")
            .ToArray();
        Assert.True(missing.Length == 0, $"{name}: operations without a 429 problem+json response: " + string.Join(", ", missing));
    }
}
