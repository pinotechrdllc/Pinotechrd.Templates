using Microsoft.Extensions.Options;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// Checks that an invalid setting stops the host, wherever the exception surfaces.
/// <para>
/// A failed start is captured exactly once, by <see cref="Capture"/>, and every assertion reads what was captured: a
/// failed factory is never touched a second time. The capture also reads the host's own log of the failure, because
/// the testing library races with the application's entry point: the entry point disposes the host that failed to
/// start, and the factory reports either the original failure or, when it reads the host after that disposal, an
/// <see cref="ObjectDisposedException"/> for its service provider. The host logged the original failure ("Hosting
/// failed to start", with the exception) before it was disposed, so it is always available there.
/// </para>
/// </summary>
internal static class StartupFailure
{
    /// <summary>A start that failed: what the factory threw and every exception the failure produced, original ones included.</summary>
    /// <param name="Thrown">What the single start attempt threw.</param>
    /// <param name="All">The thrown exception chain plus the exceptions the host logged while it failed.</param>
    internal sealed record Captured(Exception Thrown, IReadOnlyList<Exception> All)
    {
        /// <summary>Every text of every exception (messages and full renderings), for secrecy checks.</summary>
        public string Everything => string.Join(Environment.NewLine, All.Select(e => e + Environment.NewLine + e.Message));
    }

    /// <summary>
    /// Starts the host once, asserts that it fails and returns the failure. The factory must not be used afterwards
    /// (other than disposing it).
    /// </summary>
    public static Captured Capture(ApiHostFactory factory)
    {
        var sink = factory.CaptureLogs();
        var thrown = Record.Exception(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.NotNull(thrown);
        List<Exception> all = [.. Chain(thrown), .. sink.Events.Where(e => e.Exception is not null).SelectMany(e => Chain(e.Exception!))];
        return new Captured(thrown, all);
    }

    /// <summary>The first exception of <paramref name="failure"/> that is an <see cref="OptionsValidationException"/>, asserted to exist and to name <paramref name="settingName"/>.</summary>
    public static OptionsValidationException AssertOptionsInvalid(Captured failure, string settingName)
    {
        var validation = failure.All.OfType<OptionsValidationException>().FirstOrDefault(v => v.Message.Contains(settingName, StringComparison.Ordinal));
        Assert.True(validation is not null, $"Expected an OptionsValidationException naming '{settingName}' but got: {failure.Thrown}");
        return validation;
    }

    /// <summary>
    /// As <see cref="AssertOptionsInvalid(ApiHostFactory, string)"/>, and no text of the whole failure (messages, inner
    /// exceptions, stack traces) contains any of <paramref name="forbidden"/>, a secret that must never be quoted.
    /// </summary>
    public static string AssertOptionsInvalidWithout(ApiHostFactory factory, string settingName, params string[] forbidden)
    {
        var failure = Capture(factory);
        var validation = AssertOptionsInvalid(failure, settingName);
        foreach (var text in forbidden)
        {
            Assert.DoesNotContain(text, failure.Everything, StringComparison.Ordinal);
        }

        return validation.Message;
    }

    /// <summary>
    /// Starts the host and asserts that it fails with an <see cref="OptionsValidationException"/>, possibly wrapped,
    /// whose message contains <paramref name="settingName"/>.
    /// </summary>
    public static string AssertOptionsInvalid(ApiHostFactory factory, string settingName) =>
        AssertOptionsInvalid(Capture(factory), settingName).Message;

    /// <summary>As <see cref="AssertOptionsInvalid(ApiHostFactory, string)"/>, and the message also contains the offending value.</summary>
    public static string AssertOptionsInvalid(ApiHostFactory factory, string settingName, string offendingValue)
    {
        var message = AssertOptionsInvalid(factory, settingName);
        Assert.Contains(offendingValue, message, StringComparison.Ordinal);
        return message;
    }

    /// <summary>Starts the host once, asserts that it fails, and returns the first exception whose message contains <paramref name="text"/>.</summary>
    public static Exception AssertFailsNaming(ApiHostFactory factory, string text)
    {
        var failure = Capture(factory);
        var named = failure.All.FirstOrDefault(e => e.Message.Contains(text, StringComparison.Ordinal));
        Assert.True(named is not null, $"Expected a failure naming '{text}' but got: {failure.Thrown}");
        return named;
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
