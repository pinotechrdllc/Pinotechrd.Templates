using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Contract;

// The committed contract must be what the application describes. The application is hosted as it is, without the
// test-only fixture endpoints of ApiHostFactory, so the comparison covers exactly the real API surface.
public class ContractIsCurrentTests : IClassFixture<ContractIsCurrentTests.RealApiFactory>
{
    private readonly RealApiFactory _factory;

    public ContractIsCurrentTests(RealApiFactory factory) => _factory = factory;

    // Breaks when an endpoint, request, response or schema changes without regenerating contract/openapi.v1.json.
    [Fact]
    public async Task ServedDocumentEqualsTheCommittedContract()
    {
        var served = await FetchAsync();

        var message = OpenApiContract.Compare(OpenApiContract.ReadCommitted(), served);

        Assert.True(message is null, message);
    }

    // Breaks when the host does not serve the document or the contract is compared against an empty document.
    [Fact]
    public async Task ServedDocumentDescribesTheRealOperations()
    {
        var served = await FetchAsync();

        var ids = OpenApiContract.Operations(served).Select(o => o.Operation["operationId"]?.GetValue<string>()).ToArray();

        Assert.Contains("GetVersion", ids);
        Assert.DoesNotContain(OpenApiContract.Paths(served), p => p.Key.Contains("problem-fixtures", StringComparison.Ordinal));
    }

    // Breaks when the generated title is not the root name of the project followed by " API".
    [Fact]
    public async Task DocumentTitleIsTheRootNameOfTheProjectFollowedByApi()
    {
        var served = await FetchAsync();
        var committed = OpenApiContract.ReadCommitted();

        var expected = OpenApiContract.ExpectedTitle;

        Assert.Equal(expected, served["info"]!["title"]!.GetValue<string>());
        Assert.Equal(expected, committed["info"]!["title"]!.GetValue<string>());
        Assert.DoesNotContain(OpenApiContract.ApiProjectName, expected, StringComparison.Ordinal);
    }

    // Breaks when an operation falls back to the assembly name as its tag, or has none.
    [Fact]
    public async Task ServedOperationsHaveTagsAndNoneIsTheAssemblyName()
    {
        var served = await FetchAsync();

        OpenApiDocumentRules.AssertTags(served);
        Assert.Equal(["System"], OpenApiContract.Tags(OpenApiContract.Paths(served)["/api/v1/version"]!["get"]!.AsObject()));
    }

    // Breaks when the order of the paths depends on anything but the endpoints, so the byte-stable file would churn.
    [Fact]
    public async Task ServedPathsAppearInTheOrderOfTheCommittedContract()
    {
        var served = await FetchAsync();

        var expected = OpenApiContract.Paths(OpenApiContract.ReadCommitted()).Select(p => p.Key).ToArray();
        var actual = OpenApiContract.Paths(served).Select(p => p.Key).ToArray();

        Assert.Equal(expected, actual);
    }

    // The comparison is only worth something if it fails: an altered copy must be reported with where and how to fix it.
    [Fact]
    public async Task ComparisonNamesTheDifferingPathAndSchemaAndTheRegenerationCommand()
    {
        var served = await FetchAsync();
        var altered = OpenApiContract.ReadCommitted();
        altered["paths"]!.AsObject().Remove("/api/v1/version");
        altered["paths"]!.AsObject()["/api/v1/ghost"] = new JsonObject();
        altered["components"]!["schemas"]!.AsObject().Remove("ProblemDetails");

        var message = OpenApiContract.Compare(altered, served);

        Assert.NotNull(message);
        Assert.Contains("/api/v1/version", message, StringComparison.Ordinal);
        Assert.Contains("/api/v1/ghost", message, StringComparison.Ordinal);
        Assert.Contains("ProblemDetails", message, StringComparison.Ordinal);
        Assert.Contains($"dotnet build src/{ApiAssembly.Assembly.GetName().Name} -p:UpdateContract=true", message, StringComparison.Ordinal);
    }

    // Formatting and servers are not part of the comparison.
    [Fact]
    public async Task ComparisonIgnoresServersAndFormatting()
    {
        var served = await FetchAsync();
        var committed = OpenApiContract.Parse(OpenApiContract.ReadCommitted().ToJsonString());
        committed["servers"] = new JsonArray(new JsonObject { ["url"] = "http://localhost:5000" });

        Assert.Null(OpenApiContract.Compare(committed, served));
    }

    private async Task<JsonObject> FetchAsync()
    {
        using var client = _factory.CreateClient();
        using var response = await client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return OpenApiContract.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    /// <summary>The application as it is, in Development because the document is not served in Production.</summary>
    public sealed class RealApiFactory : ApiHostBase
    {
        protected override void ConfigureHost(IWebHostBuilder builder) => builder.UseEnvironment(Environments.Development)
                // A valid connection string that is never opened: the document needs the services, not a database.
                .UseSetting(MyApp.BuildingBlocks.Infrastructure.Database.DatabaseRegistration.ConnectionStringKey, TestDatabase.PlaceholderConnectionString);
    }
}
