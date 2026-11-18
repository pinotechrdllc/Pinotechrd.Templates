using System.Runtime.CompilerServices;
using Serilog.Core;
using Serilog.Events;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>
/// Remembers the exceptions the host has already logged as a failure ("Hosting failed to start", at Error or above),
/// so <see cref="BootstrapLogger"/> does not write the same exception a second time when it reaches the entry point.
/// The exception object itself is the key: the host logs the exception it then throws, and an object cannot be
/// confused with another host's in the same process. Entries go away with the exception.
/// </summary>
internal sealed class HostFailures : ILogEventSink
{
    private const string HostCategory = "Microsoft.Extensions.Hosting.Internal.Host";

    private static readonly ConditionalWeakTable<Exception, object> Logged = [];

    /// <summary>The sink that each host's logger writes to.</summary>
    public static HostFailures Recorder { get; } = new();

    /// <summary>Whether the host has already logged <paramref name="exception"/>.</summary>
    public static bool WasLogged(Exception exception) => Logged.TryGetValue(exception, out _);

    /// <inheritdoc />
    public void Emit(LogEvent logEvent)
    {
        if (logEvent is { Exception: { } exception, Level: >= LogEventLevel.Error }
            && logEvent.Properties.TryGetValue(Constants.SourceContextPropertyName, out var source)
            && source is ScalarValue { Value: HostCategory })
        {
            Logged.AddOrUpdate(exception, Recorder);
        }
    }
}
