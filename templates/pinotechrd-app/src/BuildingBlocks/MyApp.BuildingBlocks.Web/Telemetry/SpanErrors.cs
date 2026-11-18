using System.Diagnostics;
using OpenTelemetry;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>
/// Keeps exception text out of every span, from any source: a library that records a failure (Npgsql does on a failed
/// command) would otherwise send the exception message and its full <see cref="Exception.ToString"/> to the trace
/// backend, and those quote input values, user names and host names. A span keeps the fact that it failed (its error
/// status and <c>error.type</c>) and the exception type, never the text.
/// <list type="bullet">
/// <item><description><see cref="Activity.AddException"/>: the <c>exception</c> event is written with
/// <c>exception.type</c> only; <c>exception.message</c> and <c>exception.stacktrace</c> are left empty. The event itself
/// cannot be removed (the platform offers no way to), so it stays, with the type.</description></item>
/// <item><description>The error status description, which a library may set to the exception message, is replaced by
/// the span's <c>error.type</c>, or removed when it has none.</description></item>
/// </list>
/// </summary>
internal static class SpanErrors
{
    private const string ErrorTypeTag = "error.type";

    // Process-wide, like activity sources themselves: one listener serves every host in the process.
    private static readonly Lazy<ActivityListener> Redaction = new(CreateRedaction);

    /// <summary>Starts redacting the exceptions recorded on activities, once per process.</summary>
    public static void RedactRecordedExceptions() => _ = Redaction.Value;

    /// <summary>
    /// A new processor that clears error status descriptions, one per tracer provider (a provider shuts its processors
    /// down when it is disposed); add it before any exporter.
    /// </summary>
    public static BaseProcessor<Activity> CreateStatusRedaction() => new ErrorStatusProcessor();

    private static ActivityListener CreateRedaction()
    {
        // It samples nothing (None is the lowest result, so it never changes what another listener decides); it only
        // supplies the message and stack trace tags, so the platform does not add its own.
        var listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.None,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.None,
            ExceptionRecorder = (Activity _, Exception _, ref TagList tags) =>
            {
                tags.Add("exception.message", null);
                tags.Add("exception.stacktrace", null);
            },
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private sealed class ErrorStatusProcessor : BaseProcessor<Activity>
    {
        public override void OnEnd(Activity data)
        {
            if (data.Status == ActivityStatusCode.Error && data.StatusDescription is not null)
            {
                data.SetStatus(ActivityStatusCode.Error, data.GetTagItem(ErrorTypeTag) as string);
            }
        }
    }
}
