using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web.Telemetry;

namespace MyApp.Api.Tests.Telemetry;

// A startup failure is written as one Fatal event and the process exits with a non-zero code. The in-process
// tests call the function that writes the event with the console captured; one test starts the built application.
public class StartupFailureLoggingTests
{
    /// <summary>The test project is its own entry assembly (an xUnit v3 test project is an executable).</summary>
    private static Assembly Entry => Assembly.GetEntryAssembly() ?? throw new InvalidOperationException("No entry assembly.");

    // Console.Out is process-wide and other tests' hosts write their events to it too, so the captured text is searched
    // for the event of this test by the unique message of its exception.
    private static readonly object ConsoleGate = new();

    private static (T Result, List<JsonElement> Events) CaptureConsole<T>(Func<T> action, string marker)
    {
        // One capture at a time: two overlapping swaps would restore each other's writer and lose events.
        string captured;
        T result;
        lock (ConsoleGate)
        {
            var original = Console.Out;
            var text = new StringWriter();
            var synchronizedText = TextWriter.Synchronized(text);
            Console.SetOut(synchronizedText);
            try
            {
                result = action();
            }
            finally
            {
                Console.SetOut(original);
            }

            // Another test's host may still write to the writer it fetched before the swap back: read under the
            // wrapper's own lock.
            lock (synchronizedText)
            {
                captured = text.ToString();
            }
        }

        var events = new List<JsonElement>();
        foreach (var line in captured.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Contains(marker, StringComparison.Ordinal))
            {
                using var document = JsonDocument.Parse(line);
                events.Add(document.RootElement.Clone());
            }
        }

        return (result, events);
    }

    // Breaks when the startup exception is not written, is written more than once, is not Fatal, has no exception text,
    // or the entry point is not told to exit with a non-zero code.
    [Fact]
    public void WriteFatalWritesOneFatalEventWithTheExceptionAndReturnsANonZeroExitCode()
    {
        var marker = $"startup-failure-{Guid.NewGuid():N}";

        var (code, events) = CaptureConsole(
            () => BootstrapLogger.WriteFatal(new InvalidOperationException(marker), Entry),
            marker);

        Assert.NotEqual(0, code);
        Assert.Equal(1, code);
        var fatal = Assert.Single(events);
        Assert.Equal("Fatal", fatal.GetProperty("@l").GetString());
        Assert.Contains(marker, fatal.GetProperty("@x").GetString(), StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), fatal.GetProperty("@x").GetString(), StringComparison.Ordinal);
    }

    // Breaks when the bootstrap event misses the application properties the other events carry, so that it could not be
    // found by application or environment in the log platform.
    [Fact]
    public void FatalEventCarriesTheApplicationNameEnvironmentAndVersion()
    {
        var marker = $"startup-failure-{Guid.NewGuid():N}";

        var (_, events) = CaptureConsole(() => BootstrapLogger.WriteFatal(new InvalidOperationException(marker), Entry), marker);

        var fatal = Assert.Single(events);
        Assert.Equal(Entry.GetName().Name, fatal.GetProperty(TelemetryRegistration.ApplicationProperty).GetString());
        Assert.False(string.IsNullOrWhiteSpace(fatal.GetProperty(TelemetryRegistration.EnvironmentProperty).GetString()));
        Assert.False(string.IsNullOrWhiteSpace(fatal.GetProperty(TelemetryRegistration.VersionProperty).GetString()));
    }

    // Breaks when the code run by another process (a test host, the build-time OpenAPI generator) no longer sees the real
    // exception: the XML documentation says it is written, then thrown again. The event is still written once.
    [Fact]
    public void WriteFatalThrowsTheExceptionAgainWhenTheEntryPointIsRunByAnotherProcess()
    {
        var marker = $"startup-failure-{Guid.NewGuid():N}";
        var failure = new InvalidOperationException(marker);

        var (thrown, events) = CaptureConsole(
            () => Record.Exception(() => BootstrapLogger.WriteFatal(failure, typeof(Program).Assembly)),
            marker);

        Assert.Same(failure, thrown);
        Assert.Equal("Fatal", Assert.Single(events).GetProperty("@l").GetString());
    }

    // Breaks when the deliberate abort that tools use to stop the entry point is reported as a failure (a Fatal event
    // for a build that worked) or swallowed instead of thrown again.
    [Fact]
    public void HostAbortedExceptionIsThrownAgainAndNeverWritten()
    {
        var marker = $"startup-failure-{Guid.NewGuid():N}";
        var aborted = new HostAbortedException(marker);

        var (thrown, events) = CaptureConsole(
            () => Record.Exception(() => BootstrapLogger.WriteFatal(aborted, Entry)),
            marker);

        Assert.Same(aborted, thrown);
        Assert.Empty(events);
    }

    // Breaks when the arguments are not checked, so a null exception writes an event with no cause.
    [Fact]
    public void WriteFatalRejectsMissingArguments()
    {
        Assert.Throws<ArgumentNullException>(() => BootstrapLogger.WriteFatal(null!, Entry));
        Assert.Throws<ArgumentNullException>(() => BootstrapLogger.WriteFatal(new InvalidOperationException("x"), null!));
    }

    private static string DotnetHost()
    {
        var fromBuild = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");
        if (!string.IsNullOrEmpty(fromBuild) && File.Exists(fromBuild))
        {
            return fromBuild;
        }

        // <root>/shared/Microsoft.NETCore.App/<version>/ is the runtime directory; the muxer is in <root>.
        var runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        var candidate = Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        return File.Exists(candidate) ? candidate : "dotnet";
    }

    // Breaks when an invalid setting no longer stops the built application with a Fatal event and a non-zero exit code:
    // the one test that proves the process-level behavior (the exit code of Program.cs), which nothing in-process can.
    [Fact]
    public async Task BuiltApplicationWithAnInvalidSettingWritesOneFatalEventAndExitsWithANonZeroCode()
    {
        var dll = typeof(Program).Assembly.Location;
        Assert.True(File.Exists(dll), $"The built application was not found at {dll}.");
        var start = new ProcessStartInfo(DotnetHost())
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(dll);
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:0";
        start.Environment["Http__Cors__AllowedOrigins__0"] = "*";
        // Valid, so the invalid CORS setting is the only reason to stop.
        start.Environment["ConnectionStrings__Database"] = TestDatabase.PlaceholderConnectionString;
        start.Environment.Remove("OTEL_EXPORTER_OTLP_ENDPOINT");

        using var process = Process.Start(start) ?? throw new InvalidOperationException("The application did not start.");
        var output = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var error = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail("The application did not stop within 60 seconds with an invalid setting.");
        }

        var lines = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var fatal = Assert.Single(lines, l => l.Contains("\"@l\":\"Fatal\"", StringComparison.Ordinal));
        using var document = JsonDocument.Parse(fatal);
        var exception = document.RootElement.GetProperty("@x").GetString();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Equal(1, process.ExitCode);
        Assert.Contains("Http:Cors:AllowedOrigins", exception, StringComparison.Ordinal);
        Assert.Equal(typeof(Program).Assembly.GetName().Name, document.RootElement.GetProperty(TelemetryRegistration.ApplicationProperty).GetString());
        // The failure is the event, not an unstructured stack trace on standard error.
        Assert.DoesNotContain("   at ", await error, StringComparison.Ordinal);
    }

    // Breaks when a requested stop during start is reported as a failure, when a cancellation that is not a requested
    // stop (a timeout) is hidden, or when any other exception is hidden because a stop was requested.
    [Theory]
    [InlineData(true, typeof(OperationCanceledException), false, BootstrapLogger.StoppedExitCode)]
    [InlineData(false, typeof(OperationCanceledException), true, BootstrapLogger.FailureExitCode)]
    [InlineData(true, typeof(InvalidOperationException), true, BootstrapLogger.FailureExitCode)]
    [InlineData(false, typeof(InvalidOperationException), true, BootstrapLogger.FailureExitCode)]
    public void StopRequestDecidesWhetherACancellationIsAFailure(bool stopRequested, Type type, bool written, int exitCode)
    {
        var marker = $"startup-failure-{Guid.NewGuid():N}";
        var exception = (Exception)Activator.CreateInstance(type, marker)!;

        var (code, events) = CaptureConsole(() => BootstrapLogger.WriteFatal(exception, Entry, stopRequested), marker);

        Assert.Equal(exitCode, code);
        Assert.Equal(0, BootstrapLogger.StoppedExitCode);
        Assert.Equal(1, BootstrapLogger.FailureExitCode);
        if (written)
        {
            Assert.Equal("Fatal", Assert.Single(events).GetProperty("@l").GetString());
        }
        else
        {
            Assert.Empty(events);
        }
    }

    private sealed class FailingStartService(string message) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => throw new InvalidOperationException(message);

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    // Breaks when an exception that the host has already logged ("Hosting failed to start") is written a second time
    // by the entry point (two events for one failure), or when the exit code is no longer the failure code.
    [Fact]
    public void ExceptionTheHostAlreadyLoggedIsNotWrittenAgainAndStillExitsWithFailure()
    {
        var marker = $"startup-failure-{Guid.NewGuid():N}";
        using var factory = new ApiHostFactory().WithServices(services => services.AddSingleton<IHostedService>(new FailingStartService(marker)));
        var sink = factory.CaptureLogs();

        var failure = Record.Exception(() =>
        {
            using var client = factory.CreateClient();
        });

        Assert.NotNull(failure);
        var logged = Assert.Single(sink.Events, e => e.Exception is not null && e.Exception.Message == marker);
        Assert.Equal(Serilog.Events.LogEventLevel.Error, logged.Level);
        var (code, events) = CaptureConsole(() => BootstrapLogger.WriteFatal(logged.Exception!, Entry, stopRequested: false), marker);
        Assert.Equal(BootstrapLogger.FailureExitCode, code);
        Assert.Empty(events);
    }
}
