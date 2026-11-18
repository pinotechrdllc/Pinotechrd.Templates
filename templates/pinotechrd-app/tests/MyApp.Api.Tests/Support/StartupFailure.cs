using Microsoft.Extensions.Options;

namespace MyApp.Api.Tests.Support;

/// <summary>Checks that an invalid setting stops the host, wherever the exception surfaces.</summary>
internal static class StartupFailure
{
    /// <summary>
    /// Starts the host and asserts that it fails with an <see cref="OptionsValidationException"/>, possibly wrapped,
    /// whose message contains <paramref name="settingName"/>.
    /// </summary>
    public static string AssertOptionsInvalid(ApiHostFactory factory, string settingName)
    {
        var exception = Record.Exception(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.NotNull(exception);
        var validation = Chain(exception).OfType<OptionsValidationException>().FirstOrDefault();
        Assert.True(validation is not null, "Expected an OptionsValidationException but got: " + exception);
        Assert.Contains(settingName, validation.Message, StringComparison.Ordinal);
        return validation.Message;
    }

    /// <summary>As <see cref="AssertOptionsInvalid(ApiHostFactory, string)"/>, and the message also contains the offending value.</summary>
    public static string AssertOptionsInvalid(ApiHostFactory factory, string settingName, string offendingValue)
    {
        var message = AssertOptionsInvalid(factory, settingName);
        Assert.Contains(offendingValue, message, StringComparison.Ordinal);
        return message;
    }

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        yield return exception;
        IEnumerable<Exception> inner = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : exception.InnerException is null ? [] : [exception.InnerException];
        foreach (var child in inner.SelectMany(Chain))
        {
            yield return child;
        }
    }
}
