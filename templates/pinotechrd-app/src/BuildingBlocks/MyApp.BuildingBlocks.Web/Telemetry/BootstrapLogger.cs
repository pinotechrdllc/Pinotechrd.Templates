using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Formatting.Compact;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>
/// Writes the exception that stops the application before its host exists or after it fails (an invalid setting, for
/// example) as one Fatal event in the same JSON format as every other log event, so it reaches the log pipeline
/// instead of an unstructured stack trace on standard error.
/// </summary>
public static class BootstrapLogger
{
    /// <summary>The exit code of a process stopped by an exception.</summary>
    public const int FailureExitCode = 1;

    /// <summary>The exit code of a process that was asked to stop while it was starting.</summary>
    public const int StoppedExitCode = 0;

    /// <summary>
    /// As <see cref="WriteFatal(Exception, Assembly, bool)"/>, for a failure that is not a requested stop.
    /// </summary>
    /// <param name="exception">The exception that stopped the application.</param>
    /// <param name="applicationAssembly">The assembly that holds the entry point.</param>
    /// <returns><see cref="FailureExitCode"/>.</returns>
    public static int WriteFatal(Exception exception, Assembly applicationAssembly) =>
        WriteFatal(exception, applicationAssembly, stopRequested: false);

    /// <summary>
    /// Writes <paramref name="exception"/> as a Fatal event to standard output and returns
    /// <see cref="FailureExitCode"/>, for the entry point to return. When the entry point is run by another process's
    /// code instead (a test host or the build-time OpenAPI generator, which start the application through its entry
    /// point), the exception is thrown again after it is handled, so that code sees the real cause.
    /// <list type="bullet">
    /// <item><description>A <see cref="HostAbortedException"/>, which those tools throw on purpose to stop the entry
    /// point, is thrown again without being written.</description></item>
    /// <item><description>An <see cref="OperationCanceledException"/> when <paramref name="stopRequested"/> is true is a
    /// stop requested during start (a shutdown signal): it is not written and the result is
    /// <see cref="StoppedExitCode"/>. Any other cancellation, a timeout during start for example, is a failure.</description></item>
    /// <item><description>An exception the host has already logged ("Hosting failed to start") is not written again;
    /// the result is still <see cref="FailureExitCode"/>.</description></item>
    /// </list>
    /// </summary>
    /// <param name="exception">The exception that stopped the application.</param>
    /// <param name="applicationAssembly">The assembly that holds the entry point.</param>
    /// <param name="stopRequested">Whether the host's <see cref="IHostApplicationLifetime.ApplicationStopping"/> was
    /// cancelled, read from the token taken once the host is built; false before.</param>
    /// <returns><see cref="FailureExitCode"/>, or <see cref="StoppedExitCode"/> for a requested stop.</returns>
    public static int WriteFatal(Exception exception, Assembly applicationAssembly, bool stopRequested)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ArgumentNullException.ThrowIfNull(applicationAssembly);

        var stopped = stopRequested && exception is OperationCanceledException;
        if (exception is not HostAbortedException && !stopped && !HostFailures.WasLogged(exception))
        {
            var application = ApplicationInfo.Before(applicationAssembly);
            using var logger = application.Enrich(new LoggerConfiguration())
                .WriteTo.Console(new RenderedCompactJsonFormatter())
                .CreateLogger();
            logger.Fatal(exception, "The application stopped because of an unhandled exception.");
        }

        if (exception is HostAbortedException || Assembly.GetEntryAssembly() != applicationAssembly)
        {
            ExceptionDispatchInfo.Throw(exception);
        }

        return stopped ? StoppedExitCode : FailureExitCode;
    }
}
