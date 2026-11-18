using MyApp.BuildingBlocks.Domain;

namespace MyApp.BuildingBlocks.Tests.Domain;

public class ErrorTests
{
    public static TheoryData<ErrorKind, Func<string, string, Error>> Factories => new()
    {
        { ErrorKind.Failure, Error.Failure },
        { ErrorKind.NotFound, Error.NotFound },
        { ErrorKind.Conflict, Error.Conflict },
        { ErrorKind.Unauthorized, Error.Unauthorized },
        { ErrorKind.Forbidden, Error.Forbidden },
    };

    [Theory]
    [MemberData(nameof(Factories))]
    public void FactoryProducesTheMatchingKindCodeAndMessage(ErrorKind kind, Func<string, string, Error> factory)
    {
        var error = factory("orders.code", "Something happened.");

        Assert.Equal(kind, error.Kind);
        Assert.Equal("orders.code", error.Code);
        Assert.Equal("Something happened.", error.Message);
    }

    [Theory]
    [MemberData(nameof(Factories))]
    public void ErrorWithoutDetailsExposesAnEmptyNonNullCollection(ErrorKind kind, Func<string, string, Error> factory)
    {
        var error = factory("code", "message");

        Assert.Equal(kind, error.Kind);
        Assert.NotNull(error.Details);
        Assert.Empty(error.Details);
    }

    [Fact]
    public void ValidationProducesTheValidationKindCodeAndMessage()
    {
        var error = Error.Validation("validation.code", "Invalid input.");

        Assert.Equal(ErrorKind.Validation, error.Kind);
        Assert.Equal("validation.code", error.Code);
        Assert.Equal("Invalid input.", error.Message);
    }

    [Fact]
    public void ValidationWithoutDetailsExposesAnEmptyNonNullCollection()
    {
        var error = Error.Validation("code", "message");

        Assert.NotNull(error.Details);
        Assert.Empty(error.Details);
    }

    [Fact]
    public void ValidationWithEmptyDetailsExposesAnEmptyNonNullCollection()
    {
        var error = Error.Validation("code", "message", new Dictionary<string, string[]>());

        Assert.NotNull(error.Details);
        Assert.Empty(error.Details);
    }

    [Fact]
    public void ValidationCarriesItsPerFieldDetails()
    {
        var details = new Dictionary<string, string[]>
        {
            ["name"] = ["Required.", "Too short."],
            ["age"] = ["Must be positive."],
        };

        var error = Error.Validation("code", "message", details);

        Assert.Equal(2, error.Details.Count);
        Assert.Equal(["Required.", "Too short."], error.Details["name"]);
        Assert.Equal(["Must be positive."], error.Details["age"]);
    }

    [Fact]
    public void ValidationIsNotChangedByLaterChangesToTheDictionaryThatWasPassedIn()
    {
        var details = new Dictionary<string, string[]>
        {
            ["name"] = ["Required."],
            ["age"] = ["Must be positive."],
        };
        var error = Error.Validation("code", "message", details);

        details["email"] = ["Invalid."];
        details.Remove("age");
        details["name"] = ["Replaced."];

        Assert.Equal(2, error.Details.Count);
        Assert.False(error.Details.ContainsKey("email"));
        Assert.Equal(["Must be positive."], error.Details["age"]);
        Assert.Equal(["Required."], error.Details["name"]);
    }

    [Fact]
    public void ValidationIsNotChangedByLaterChangesToAnArrayThatWasPassedIn()
    {
        var messages = new[] { "Required.", "Too short." };
        var error = Error.Validation("code", "message", new Dictionary<string, string[]> { ["name"] = messages });

        messages[0] = "Changed.";

        Assert.Equal(["Required.", "Too short."], error.Details["name"]);
    }
}
