using MyApp.BuildingBlocks.Domain;

namespace MyApp.BuildingBlocks.Tests.Domain;

public class ResultTests
{
    private static readonly Error SampleError = Error.Failure("sample.code", "Sample message.");

    [Fact]
    public void SuccessIsSuccessAndNotFailure()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
    }

    [Fact]
    public void FailureCarriesItsErrorAndIsNotSuccess()
    {
        var result = Result.Failure(SampleError);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Same(SampleError, result.Error);
    }

    [Fact]
    public void TypedSuccessExposesItsValue()
    {
        var result = Result.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void TypedFailureCarriesItsErrorAndIsNotSuccess()
    {
        var result = Result.Failure<int>(SampleError);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Same(SampleError, result.Error);
    }

    [Fact]
    public void CreatingAFailureWithoutAnErrorThrows()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void CreatingATypedFailureWithoutAnErrorThrows()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure<string>(null!));
    }

    [Fact]
    public void ConvertingANullErrorToAResultThrows()
    {
        Error? none = null;

        Assert.Throws<ArgumentNullException>(() => { Result result = none!; return result; });
    }

    [Fact]
    public void ConvertingANullErrorToATypedResultThrows()
    {
        Error? none = null;

        Assert.Throws<ArgumentNullException>(() => { Result<int> result = none!; return result; });
    }

    [Fact]
    public void SuccessWithANullReferenceValueIsASuccessWhoseValueIsNull()
    {
        var result = Result.Success<string?>(null);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Value);
    }

    [Fact]
    public void ReadingTheValueOfAFailureThrows()
    {
        var result = Result.Failure<int>(SampleError);

        Assert.Throws<InvalidOperationException>(() => result.Value);
    }

    [Fact]
    public void ReadingTheErrorOfASuccessThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Result.Success().Error);
    }

    [Fact]
    public void ReadingTheErrorOfATypedSuccessThrows()
    {
        Assert.Throws<InvalidOperationException>(() => Result.Success("value").Error);
    }

    [Fact]
    public void ValueConvertsImplicitlyToASuccessfulTypedResult()
    {
        Result<string> result = "hello";

        Assert.True(result.IsSuccess);
        Assert.Equal("hello", result.Value);
    }

    [Fact]
    public void ErrorConvertsImplicitlyToAFailedResult()
    {
        Result result = SampleError;

        Assert.True(result.IsFailure);
        Assert.Same(SampleError, result.Error);
    }

    [Fact]
    public void ErrorConvertsImplicitlyToAFailedTypedResult()
    {
        Result<int> result = SampleError;

        Assert.True(result.IsFailure);
        Assert.Same(SampleError, result.Error);
        Assert.Throws<InvalidOperationException>(() => result.Value);
    }
}
