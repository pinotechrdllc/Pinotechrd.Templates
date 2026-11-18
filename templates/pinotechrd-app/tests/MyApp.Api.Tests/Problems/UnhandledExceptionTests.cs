using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;

namespace MyApp.Api.Tests.Problems;

// Breaks if the handler echoes the exception message, type name or stack trace in any environment, or answers
// something other than 500 internal_error.
public class UnhandledExceptionTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task UnhandledExceptionAnswers500InternalErrorWithoutLeakingDetails(string environment)
    {
        await using var factory = ApiHostFactory.ForEnvironment(environment);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync($"{ProblemFixtureEndpoint.Base}/throw", TestContext.Current.CancellationToken);

        await ProblemAssert.IsProblemAsync(response, 500, "internal_error");
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.DoesNotContain(ProblemFixtureEndpoint.Secret, text, StringComparison.Ordinal);
        Assert.DoesNotContain(nameof(InvalidOperationException), text, StringComparison.Ordinal);
        Assert.DoesNotContain(" at ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("StackTrace", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(nameof(ProblemFixtureEndpoint), text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public void FactoryRunsInTheRequestedEnvironment(string environment)
    {
        using var factory = ApiHostFactory.ForEnvironment(environment);

        var host = factory.Services.GetRequiredService<IWebHostEnvironment>();

        Assert.Equal(environment, host.EnvironmentName);
    }
}
