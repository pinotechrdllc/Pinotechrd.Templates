using System.Net;
using System.Net.Http.Headers;
using System.Text;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Domain;

namespace MyApp.Api.Tests.Problems;

// Covers the review changes of the problem details contract, through the real host.
public class ProblemContractTests(ApiHostFactory factory) : IClassFixture<ApiHostFactory>
{
    private const string Base = ProblemFixtureEndpoint.Base;

    // Breaks if routing runs outside the exception handler (the ambiguous match escapes) or the
    // developer exception page writes its own body.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ExceptionThrownDuringRoutingAnswers500InternalErrorWithoutLeaks(string environment)
    {
        await using var host = ApiHostFactory.ForEnvironment(environment);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{Base}/duplicate", TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        Assert.False(body.TryGetProperty("exception", out _));
        var text = body.GetRawText();
        Assert.DoesNotContain("Ambiguous", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("duplicate", text, StringComparison.OrdinalIgnoreCase);
    }

    // Breaks if the exception member is left in the document.
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnhandledExceptionDocumentHasNoExceptionMember(string environment)
    {
        await using var host = ApiHostFactory.ForEnvironment(environment);
        using var client = host.CreateClient();

        using var response = await client.GetAsync($"{Base}/throw", TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        Assert.False(body.TryGetProperty("exception", out _));
        Assert.DoesNotContain(ProblemFixtureEndpoint.Secret, body.GetRawText(), StringComparison.Ordinal);
    }

    // Breaks if the status-to-code table loses 408 or 413 or the handler ignores the exception status.
    [Theory]
    [InlineData(413, "payload_too_large")]
    [InlineData(408, "request_timeout")]
    public async Task BadHttpRequestExceptionKeepsItsStatusWithTheMatchingCode(int status, string code)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/bad-request/{status}", TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, status, code);
    }

    // Breaks if a body-less error status without a specific code is left empty or gets another code.
    [Theory]
    [InlineData(418)]
    [InlineData(503)]
    public async Task BodylessStatusWithoutSpecificCodeAnswersRequestFailed(int status)
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/status/{status}", TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, status, "request_failed");
    }

    // Breaks if the writer declines requests whose Accept header excludes JSON.
    [Fact]
    public async Task ReturnedErrorWithAcceptHtmlStillAnswersProblemJsonWithCodeDetailAndTraceId()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Base}/errors/{ErrorKind.Conflict}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 409, ProblemFixtureEndpoint.CodeOf(ErrorKind.Conflict));
        Assert.Equal(ProblemFixtureEndpoint.MessageOf(ErrorKind.Conflict), body.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task UnknownRouteWithAcceptHtmlStillAnswersProblemJson()
    {
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/no/such/route");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 404, "not_found");
    }

    // Breaks if 422 reports the RFC 4918 title or type instead of RFC 9110.
    [Fact]
    public async Task FailureErrorAnswers422WithTheRfc9110TitleAndType()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/errors/{ErrorKind.Failure}", TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 422, ProblemFixtureEndpoint.CodeOf(ErrorKind.Failure));
        Assert.Equal("Unprocessable Content", body.GetProperty("title").GetString());
        Assert.Equal("https://www.rfc-editor.org/rfc/rfc9110#section-15.5.21", body.GetProperty("type").GetString());
    }

    // Built-in validation. Breaks if error keys keep the C# property names.
    [Fact]
    public async Task BuiltInValidationNamesFieldsAsInTheRequestJson()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent(
            """{"firstName":null,"homeAddress":{"street":null},"others":[{"street":"ok"},{"street":null}]}""",
            Encoding.UTF8,
            "application/json");

        using var response = await client.PostAsync($"{Base}/profiles", content, TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 400, "validation_failed");
        var keys = body.GetProperty("errors").EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["firstName", "homeAddress.street", "others[1].street"], keys);
    }

    // Error details. Breaks if keys are written as given or colliding keys overwrite each other.
    [Fact]
    public async Task ValidationErrorKeysAreConvertedAndCollidingKeysAreMerged()
    {
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{Base}/validation-keys", TestContext.Current.CancellationToken);

        var body = await ProblemAssert.IsProblemAsync(response, 400, "fixture_keys");
        var errors = body.GetProperty("errors");
        var keys = errors.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["lines[1].quantity", "name"], keys);
        var merged = errors.GetProperty("name").EnumerateArray().Select(e => e.GetString()).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["first", "second"], merged);
        Assert.Equal("third", errors.GetProperty("lines[1].quantity")[0].GetString());
    }

    // Breaks if the catalogue wording drops the shape case.
    [Fact]
    public void InvalidJsonCatalogueRowMentionsTheShapeOfTheBody()
    {
        var row = ProblemCodeCatalogueTests.ReadCatalogue().Single(r => r.Code == "invalid_json");

        Assert.Contains("shape", row.Meaning, StringComparison.OrdinalIgnoreCase);
    }

    // Catalogue side. Breaks if the new codes are not listed with their status.
    [Theory]
    [InlineData("payload_too_large", "413")]
    [InlineData("request_timeout", "408")]
    public void NewCodesAreListedInTheCatalogue(string code, string status)
    {
        var row = ProblemCodeCatalogueTests.ReadCatalogue().Single(r => r.Code == code);

        Assert.Equal(status, row.Status);
    }

    [Fact]
    public async Task ValidProfileIsNotAProblem()
    {
        using var client = factory.CreateClient();
        using var content = new StringContent("""{"firstName":"a","homeAddress":{"street":"s"},"others":[]}""", Encoding.UTF8, "application/json");

        using var response = await client.PostAsync($"{Base}/profiles", content, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
