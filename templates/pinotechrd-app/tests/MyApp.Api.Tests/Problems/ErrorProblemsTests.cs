using System.Text.Json;
using Microsoft.AspNetCore.Http;
using MyApp.BuildingBlocks.Domain;
using MyApp.BuildingBlocks.Web.Problems;

namespace MyApp.Api.Tests.Problems;

// Breaks if the kind-to-status table or the problem details members of ErrorProblems change.
public class ErrorProblemsTests
{
    [Theory]
    [InlineData(ErrorKind.Validation, 400)]
    [InlineData(ErrorKind.Unauthorized, 401)]
    [InlineData(ErrorKind.Forbidden, 403)]
    [InlineData(ErrorKind.NotFound, 404)]
    [InlineData(ErrorKind.Conflict, 409)]
    [InlineData(ErrorKind.Failure, 422)]
    public void EachErrorKindMapsToItsDocumentedStatus(ErrorKind kind, int expected)
    {
        Assert.Equal(expected, kind.ToStatusCode());
    }

    [Fact]
    public void NoDefinedErrorKindMapsTo500()
    {
        foreach (var kind in Enum.GetValues<ErrorKind>())
        {
            var status = kind.ToStatusCode();
            Assert.NotEqual(StatusCodes.Status500InternalServerError, status);
            Assert.InRange(status, 400, 499);
        }
    }

    [Fact]
    public void UnknownErrorKindDoesNotMapTo500()
    {
        Assert.NotEqual(500, ((ErrorKind)999).ToStatusCode());
    }

    [Theory]
    [InlineData(ErrorKind.Unauthorized)]
    [InlineData(ErrorKind.Forbidden)]
    [InlineData(ErrorKind.NotFound)]
    [InlineData(ErrorKind.Conflict)]
    [InlineData(ErrorKind.Failure)]
    public void ProblemDetailsCarriesStatusDetailAndCodeOfTheError(ErrorKind kind)
    {
        var error = kind switch
        {
            ErrorKind.Unauthorized => Error.Unauthorized("some_code", "Some message."),
            ErrorKind.Forbidden => Error.Forbidden("some_code", "Some message."),
            ErrorKind.NotFound => Error.NotFound("some_code", "Some message."),
            ErrorKind.Conflict => Error.Conflict("some_code", "Some message."),
            _ => Error.Failure("some_code", "Some message."),
        };

        var problem = error.ToProblemDetails();

        Assert.Equal(kind.ToStatusCode(), problem.Status);
        Assert.Equal("Some message.", problem.Detail);
        Assert.Equal("some_code", problem.Extensions["code"]?.ToString());
    }

    [Fact]
    public void ValidationProblemDetailsCarriesTheMessagesPerField()
    {
        var details = new Dictionary<string, string[]>
        {
            ["name"] = ["Too short."],
            ["age"] = ["Too low.", "Not a number."],
        };

        var problem = Error.Validation("bad_input", "The input is invalid.", details).ToProblemDetails();

        Assert.Equal(400, problem.Status);
        Assert.Equal("The input is invalid.", problem.Detail);
        Assert.Equal("bad_input", problem.Extensions["code"]?.ToString());
        var validation = Assert.IsAssignableFrom<HttpValidationProblemDetails>(problem);
        Assert.Equal(2, validation.Errors.Count);
        Assert.Equal(["Too short."], validation.Errors["name"]);
        Assert.Equal(["Too low.", "Not a number."], validation.Errors["age"]);
    }

    [Fact]
    public void ProblemDetailsSerializesCodeAndErrorsAsMembersOfTheDocument()
    {
        var problem = Error.Validation("bad_input", "Invalid.", new Dictionary<string, string[]> { ["name"] = ["Required."] })
            .ToProblemDetails();

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(problem, problem.GetType()));

        Assert.Equal("bad_input", document.RootElement.GetProperty("code").GetString());
        Assert.Equal(400, document.RootElement.GetProperty("status").GetInt32());
        Assert.Equal("Required.", document.RootElement.GetProperty("errors").GetProperty("name")[0].GetString());
    }
}
