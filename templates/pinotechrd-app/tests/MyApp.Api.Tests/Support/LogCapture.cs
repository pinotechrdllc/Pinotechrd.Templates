using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Web.Telemetry;
using Serilog.Core;
using Serilog.Events;

namespace MyApp.Api.Tests.Support;

/// <summary>Receives every log event of one host, the way the README says a test can: as an <see cref="ILogEventSink"/> service.</summary>
internal sealed class CollectingSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();

    public IReadOnlyList<LogEvent> Events => [.. _events];

    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}

/// <summary>Capture of the log events of a test host, and the questions the telemetry tests ask about them.</summary>
internal static class LogCapture
{
    // The names the README documents for the properties of the completion event.
    public const string MethodProperty = "RequestMethod";
    public const string PathProperty = "RequestPath";
    public const string StatusProperty = "StatusCode";
    public const string ElapsedProperty = "Elapsed";

    /// <summary>Registers a sink that keeps every event of this host (and of no other host).</summary>
    public static CollectingSink CaptureLogs(this ApiHostFactory factory)
    {
        var sink = new CollectingSink();
        factory.WithServices(services => services.AddSingleton<ILogEventSink>(sink));
        return sink;
    }

    /// <summary>The completion events: those that carry the request properties of the documented event.</summary>
    public static IReadOnlyList<LogEvent> CompletionEvents(this CollectingSink sink) =>
        [.. sink.Events.Where(IsCompletion)];

    public static bool IsCompletion(LogEvent logEvent) =>
        logEvent.Properties.ContainsKey(MethodProperty) && logEvent.Properties.ContainsKey(StatusProperty);

    /// <summary>The trace id of the event as the 32 hex digits used everywhere else, or null when it has none.</summary>
    public static string? TraceIdOf(this LogEvent logEvent) =>
        logEvent.TraceId is { } id && id != default ? id.ToHexString() : null;

    /// <summary>The category that wrote the event (its <c>SourceContext</c>), or null.</summary>
    public static string? CategoryOf(this LogEvent logEvent) => logEvent.ScalarOf("SourceContext") as string;

    /// <summary>Whether the event was written by <paramref name="category"/> or a category below it.</summary>
    public static bool IsFrom(this LogEvent logEvent, string category) =>
        logEvent.CategoryOf() is { } source
        && (string.Equals(source, category, StringComparison.Ordinal) || source.StartsWith(category + ".", StringComparison.Ordinal));

    /// <summary>The scalar value of a property, or null.</summary>
    public static object? ScalarOf(this LogEvent logEvent, string name) =>
        logEvent.Properties.TryGetValue(name, out var value) && value is ScalarValue scalar ? scalar.Value : null;

    /// <summary>
    /// Every piece of text an event holds: its rendered message, its template, the names and values of its properties
    /// (nested values included), and its exception with the inner exceptions, so a value cannot hide anywhere.
    /// </summary>
    public static IEnumerable<string> TextsOf(this LogEvent logEvent) => TextsOf(logEvent, includeException: true);

    /// <summary>As <see cref="TextsOf(LogEvent)"/> without the exception.</summary>
    public static IEnumerable<string> TextsWithoutException(this LogEvent logEvent) => TextsOf(logEvent, includeException: false);

    private static IEnumerable<string> TextsOf(LogEvent logEvent, bool includeException)
    {
        yield return logEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);
        yield return logEvent.MessageTemplate.Text;
        foreach (var property in logEvent.Properties)
        {
            yield return property.Key;
            foreach (var text in TextsOf(property.Value))
            {
                yield return text;
            }
        }

        if (includeException && logEvent.Exception is { } exception)
        {
            yield return exception.ToString();
        }
    }

    private static IEnumerable<string> TextsOf(LogEventPropertyValue value)
    {
        switch (value)
        {
            case ScalarValue scalar:
                yield return Convert.ToString(scalar.Value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                break;
            case SequenceValue sequence:
                foreach (var element in sequence.Elements.SelectMany(TextsOf))
                {
                    yield return element;
                }

                break;
            case StructureValue structure:
                yield return structure.TypeTag ?? string.Empty;
                foreach (var property in structure.Properties)
                {
                    yield return property.Name;
                    foreach (var text in TextsOf(property.Value))
                    {
                        yield return text;
                    }
                }

                break;
            case DictionaryValue dictionary:
                foreach (var pair in dictionary.Elements)
                {
                    foreach (var text in TextsOf(pair.Key).Concat(TextsOf(pair.Value)))
                    {
                        yield return text;
                    }
                }

                break;
            default:
                yield return value.ToString();
                break;
        }
    }

    /// <summary>The name that every activity source and meter of the application starts with, before its dot.</summary>
    public static string ProjectName { get; } = ProjectNameOf(typeof(Program).Assembly.GetName().Name);

    private static string ProjectNameOf(string? apiAssemblyName)
    {
        // The API assembly is "<Project>.Api"; the template replaces the placeholder in both the same way.
        const string Suffix = ".Api";
        Assert.NotNull(apiAssemblyName);
        Assert.EndsWith(Suffix, apiAssemblyName, StringComparison.Ordinal);
        return apiAssemblyName[..^Suffix.Length];
    }

    /// <summary>The documented application property names, kept here so tests do not repeat them.</summary>
    public static readonly string[] ApplicationProperties =
    [
        TelemetryRegistration.ApplicationProperty,
        TelemetryRegistration.EnvironmentProperty,
        TelemetryRegistration.VersionProperty,
    ];
}
