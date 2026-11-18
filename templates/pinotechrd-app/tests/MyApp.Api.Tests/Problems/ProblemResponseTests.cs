using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Domain;

namespace MyApp.Api.Tests.Problems;

// Every case goes through the real host and asserts the problem+json envelope (content type, status, code, traceId).
public class ProblemResponseTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private const string Base = ProblemFixtureEndpoint.Base;

    private const string ValidItem = """{"name":"abc","quantity":3}""";

    // Breaks if ToProblem stops using the kind-to-status mapping or drops the error's own code or detail.
    [Theory]
    [InlineData(ErrorKind.Validation, 400)]
    [InlineData(ErrorKind.Unauthorized, 401)]
    [InlineData(ErrorKind.Forbidden, 403)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.Failure, 422)]
    public async Task ReturnedErrorAnswersItsMappedStatusWithItsOwnCodeAndDetail(ErrorKind kind, int status)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/errors/{kind}", TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, status, ProblemFixtureEndpoint.CodeOf(kind));
        Assert.Equal(ProblemFixtureEndpoint.MessageOf(kind), body.GetProperty("detail").GetString());
    }

    // Breaks if the per-field details of a validation error are not written as the errors member.
    [Fact]
    public async Task ReturnedValidationErrorIncludesTheMessagesPerField()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/errors/{ErrorKind.Validation}", TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 400, ProblemFixtureEndpoint.CodeOf(ErrorKind.Validation));
        var errors = body.GetProperty("errors");
        Assert.Equal("Name is too short.", errors.GetProperty("name")[0].GetString());
        Assert.Equal(2, errors.GetProperty("quantity").GetArrayLength());
    }

    // Breaks if the status code pages are not part of the pipeline (empty 404) or the 404 code changes.
    [Fact]
    public async Task UnknownRouteAnswers404NotFound()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/no/such/route", TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 404, "not_found");
    }

    // Breaks if bad JSON is answered by an empty 400 or with a code other than invalid_json.
    [Fact]
    public async Task MalformedJsonBodyAnswers400InvalidJson()
    {
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, """{"name": "abc", """);

        await ProblemAssert.IsProblemAsync(response, 400, "invalid_json");
    }

    // Breaks if a JSON type mismatch is reported as bad_request or as a 500.
    [Fact]
    public async Task ValidJsonOfTheWrongShapeAnswers400InvalidJson()
    {
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, """{"name": 5, "quantity": 3}""");

        await ProblemAssert.IsProblemAsync(response, 400, "invalid_json");
    }

    [Fact]
    public async Task NonIntegerQueryValueAnswers400BadRequest()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/count?count=abc", TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 400, "bad_request");
    }

    [Fact]
    public async Task MissingRequiredQueryParameterAnswers400BadRequest()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/count", TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 400, "bad_request");
    }

    [Fact]
    public async Task ValidQueryValueIsNotAProblem()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/count?count=7", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("7", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MissingRequiredBodyAnswers400BadRequest()
    {
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, string.Empty);

        await ProblemAssert.IsProblemAsync(response, 400, "bad_request");
    }

    // Breaks if DataAnnotations validation is not wired (AddValidation) or answers another code or shape.
    [Fact]
    public async Task DataAnnotationsFailureAnswers400ValidationFailedNamingEachInvalidField()
    {
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, """{"name":"x","quantity":99}""");

        var body = await ProblemAssert.IsProblemAsync(response, 400, "validation_failed");
        var errors = body.GetProperty("errors");
        Assert.Equal(JsonValueKind.Object, errors.ValueKind);
        var fields = errors.EnumerateObject().Select(p => p.Name.ToLowerInvariant()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["name", "quantity"], fields);
        foreach (var field in errors.EnumerateObject())
        {
            Assert.True(field.Value.GetArrayLength() > 0, $"Field '{field.Name}' has no message.");
        }
    }

    [Fact]
    public async Task ValidBodyIsNotAProblem()
    {
        using var client = factory.CreateClient();

        using var response = await PostJsonAsync(client, ValidItem);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // Breaks if an existing route with the wrong verb falls through to an empty 405 or another code.
    [Fact]
    public async Task WrongMethodOnAnExistingRouteAnswers405MethodNotAllowed()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/api/v1/version", content, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 405, "method_not_allowed");
    }

    [Fact]
    public async Task UnsupportedContentTypeOnTheJsonEndpointAnswers415UnsupportedMediaType()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("name=abc", Encoding.UTF8, "text/plain");

        using var response = await client.PostAsync($"{Base}/items", content, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 415, "unsupported_media_type");
    }

    // Breaks if traceId stops following the W3C trace of the request (for example a random id is generated).
    [Fact]
    public async Task TraceIdEqualsTheTraceIdOfTheTraceparentHeader()
    {
        const string TraceId = "0af7651916cd43dd8448eb211c80319c";
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/no/such/route");
        request.Headers.Add("traceparent", $"00-{TraceId}-b7ad6b7169203331-01");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 404, "not_found");
        Assert.Equal(TraceId, body.GetProperty("traceId").GetString());
    }

    [Fact]
    public async Task TraceIdIsPresentWithoutATraceparentHeader()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/errors/{ErrorKind.Conflict}", TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 409, ProblemFixtureEndpoint.CodeOf(ErrorKind.Conflict));
    }

    // Breaks if the problem handling wraps or alters success responses.
    [Fact]
    public async Task SuccessResponseIsStillPlainJson()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/version", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(HttpClient client, string json)
    {
        using var content = new StringContent(json, Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
        return await client.PostAsync($"{Base}/items", content, TestContext.Current.CancellationToken);
    }
}
