using System.Net.Http.Headers;
using System.Text;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Domain;

namespace MyApp.Api.Tests.Problems;

// One title per status: the title names the status, whoever produced the problem.
public class ProblemTitleTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private const string Base = ProblemFixtureEndpoint.Base;

    // Breaks when the built-in validation keeps its own sentence as the title, or a validation Error gets another title.
    [Fact]
    public async Task BuiltInValidationAndAValidationErrorShareTheTitleAndTypeOfBadRequest()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("""{"name":"x","quantity":99}""", Encoding.UTF8, new MediaTypeHeaderValue("application/json"));

        using var builtIn = await client.PostAsync($"{Base}/items", content, TestContext.Current.CancellationToken);
        using var returned = await client.GetAsync($"{Base}/errors/{ErrorKind.Validation}", TestContext.Current.CancellationToken);

        var fromAnnotations = await ProblemAssert.IsProblemAsync(builtIn, 400, "validation_failed");
        var fromError = await ProblemAssert.IsProblemAsync(returned, 400, ProblemFixtureEndpoint.CodeOf(ErrorKind.Validation));
        Assert.Equal("Bad Request", fromAnnotations.GetProperty("title").GetString());
        Assert.Equal("Bad Request", fromError.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(fromAnnotations.GetProperty("type").GetString()));
        Assert.Equal(fromAnnotations.GetProperty("type").GetString(), fromError.GetProperty("type").GetString());
    }

    // Breaks when fixing the validation title changes the title of another status.
    [Theory]
    [InlineData(ErrorKind.NotFound, 404, "Not Found")]
    [InlineData(ErrorKind.Conflict, 409, "Conflict")]
    [InlineData(ErrorKind.Failure, 422, "Unprocessable Content")]
    public async Task ReturnedErrorOfAnotherKindKeepsTheTitleOfItsStatus(ErrorKind kind, int status, string title)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/errors/{kind}", TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, status, ProblemFixtureEndpoint.CodeOf(kind));
        Assert.Equal(title, body.GetProperty("title").GetString());
    }

    // Breaks when the unhandled exception answer loses the title of 500.
    [Fact]
    public async Task UnhandledExceptionKeepsTheTitleOfInternalServerError()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/throw", TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        Assert.Equal("Internal Server Error", body.GetProperty("title").GetString());
    }
}
