using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Formatting.Compact;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>
/// Structured logging with Serilog and traces and metrics with OpenTelemetry.
/// <para>
/// Logs are a stream of events on standard output, one JSON object per line, in every environment, and nothing
/// else: no files and no other destination configured in code. The levels come from the standard
/// <c>Logging:LogLevel</c> section, so an environment variable such as <c>Logging__LogLevel__Default=Debug</c>
/// changes them. Every event carries the trace and span ids of the current activity (<c>@tr</c>, <c>@sp</c>) and
/// <see cref="ApplicationProperty"/>, <see cref="EnvironmentProperty"/> and <see cref="VersionProperty"/>. Any
/// <c>Serilog.Core.ILogEventSink</c> or <c>Serilog.Core.ILogEventEnricher</c> registered as a service is added too.
/// </para>
/// <para>
/// Traces: a server span per request named after its route (<c>http.route</c>), except the health probes, and a
/// client span per outgoing <see cref="System.Net.Http.HttpClient"/> call. Metrics: the ASP.NET Core server metrics
/// (<c>http.server.request.duration</c> among them), the HTTP client metrics and the runtime metrics. Sources and
/// meters named <see cref="ApplicationSourceNames"/>, and those added with <see cref="AddTelemetrySource"/> and
/// <see cref="AddTelemetryMeter"/>, are collected as well. The resource names the service (the application name), its
/// version and its deployment environment. Traces and metrics are exported over OTLP only when
/// <see cref="OtlpEndpointKey"/> (or the endpoint of that signal) is configured; otherwise nothing leaves the process.
/// </para>
/// <para>
/// Rule: no log event or span attribute holds a header value (<c>Authorization</c>, <c>Cookie</c>, <c>Set-Cookie</c> or
/// any other), a query string, a request or response body or the client address, because they carry tokens and
/// personal data; the framework categories that would write one are pinned (<see cref="PinnedLogCategories"/>). No
/// span holds exception text: a recorded exception keeps its type only, and an error status description becomes the
/// span's <c>error.type</c> (<see cref="SpanErrors"/>). The
/// raw request path is recorded (in the completion event, the request scope and the <c>url.path</c> span attribute),
/// so routes carry opaque identifiers only, such as a UUID: never personal data (a national id, an email, a phone
/// number, a name) and never a secret. The one accepted exception is the unhandled-exception event, which holds the
/// exception in full and so whatever its message quotes. Code that logs follows the same rule: log identifiers, never
/// credentials or personal data.
/// </para>
/// </summary>
public static class TelemetryRegistration
{
    /// <summary>
    /// The activity sources and meters of the application that are collected without registration: any name that
    /// starts with the project name, such as <c>MyApp.Sales</c>.
    /// </summary>
    public const string ApplicationSourceNames = "MyApp.*";

    /// <summary>The setting, usually an environment variable, that turns on OTLP export of traces and metrics.</summary>
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>The setting that turns on OTLP export of traces only, with its own endpoint.</summary>
    public const string OtlpTracesEndpointKey = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";

    /// <summary>The setting that turns on OTLP export of metrics only, with its own endpoint.</summary>
    public const string OtlpMetricsEndpointKey = "OTEL_EXPORTER_OTLP_METRICS_ENDPOINT";

    /// <summary>The log event property with the application name.</summary>
    public const string ApplicationProperty = "Application";

    /// <summary>The log event property with the environment name.</summary>
    public const string EnvironmentProperty = "Environment";

    /// <summary>The log event property with the application version.</summary>
    public const string VersionProperty = "Version";

    /// <summary>
    /// The framework categories that write a header value, the query string or the client address. They stay at
    /// <c>Warning</c> whatever <c>Default</c> or a parent such as <c>Microsoft.AspNetCore</c> is set to, unless the
    /// category itself is named in <c>Logging:LogLevel</c>:
    /// <list type="bullet">
    /// <item><description><c>Microsoft.AspNetCore.Hosting.Diagnostics</c>: "Request starting" (Information) holds the
    /// query string; the completion event replaces it.</description></item>
    /// <item><description><c>Microsoft.AspNetCore.HttpOverrides</c>: "Unknown proxy" (Debug) holds the client address and
    /// port, "Unparsable IP" (Debug) the raw <c>X-Forwarded-For</c> value.</description></item>
    /// <item><description><c>Microsoft.AspNetCore.Cors</c>: "The request has an origin header" (Debug) and "Request
    /// origin ... does not have permission" (Information) hold the <c>Origin</c> header value, and a refused preflight
    /// names the requested headers.</description></item>
    /// <item><description><c>Microsoft.AspNetCore.Server.Kestrel.BadRequests</c>: "bad request data" (Debug) quotes the
    /// rejected part of the request, such as a <c>Host</c> header value or the request line.</description></item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> PinnedLogCategories { get; } =
    [
        "Microsoft.AspNetCore.Hosting.Diagnostics",
        "Microsoft.AspNetCore.HttpOverrides",
        "Microsoft.AspNetCore.Cors",
        "Microsoft.AspNetCore.Server.Kestrel.BadRequests",
    ];

    /// <summary>
    /// Replaces the platform's log providers with the JSON event stream and adds the OpenTelemetry traces and metrics
    /// described on <see cref="TelemetryRegistration"/>. The completion event of each request is added to the pipeline
    /// by <see cref="Security.HttpSecurityRegistration.UseApiPipeline"/>. Nothing here needs a setting or reaches the
    /// network unless OTLP export is configured. An invalid level in <c>Logging:LogLevel</c> stops the host.
    /// </summary>
    /// <typeparam name="TBuilder">The type of the host builder.</typeparam>
    /// <param name="builder">The host builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static TBuilder AddTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);

        var application = ApplicationInfo.From(builder.Environment);
        var levels = LogLevels.From(builder.Configuration);

        builder.Logging.ClearProviders();
        // The logger belongs to this host, not to the static Log.Logger, so several hosts in one process (tests) keep
        // their own sinks.
        builder.Services.AddSerilog(
            (services, logger) => application.Enrich(levels.ApplyTo(logger.MinimumLevel))
                .ReadFrom.Services(services)
                .Enrich.FromLogContext()
                .WriteTo.Console(new RenderedCompactJsonFormatter())
                .WriteTo.Sink(HostFailures.Recorder),
            preserveStaticLogger: true);

        AddOpenTelemetry(builder.Services, builder.Configuration, application);
        return builder;
    }

    /// <summary>
    /// Collects the spans of the <see cref="ActivitySource"/> named <paramref name="name"/>, for a library or a source
    /// whose name does not match <see cref="ApplicationSourceNames"/> (a database driver or a messaging library, for
    /// example). The name may end with <c>*</c>. It can be called before or after <see cref="AddTelemetry"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name of the activity source.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTelemetrySource(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return services.ConfigureOpenTelemetryTracerProvider(tracing => tracing.AddSource(name));
    }

    /// <summary>
    /// Collects the instruments of the <see cref="System.Diagnostics.Metrics.Meter"/> named <paramref name="name"/>, for
    /// a meter whose name does not match <see cref="ApplicationSourceNames"/>. The name may end with <c>*</c>. It can be
    /// called before or after <see cref="AddTelemetry"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="name">The name of the meter.</param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddTelemetryMeter(this IServiceCollection services, string name)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return services.ConfigureOpenTelemetryMeterProvider(metrics => metrics.AddMeter(name));
    }

    private static void AddOpenTelemetry(IServiceCollection services, IConfiguration configuration, ApplicationInfo application)
    {
        var exportTraces = IsSet(configuration, OtlpEndpointKey) || IsSet(configuration, OtlpTracesEndpointKey);
        var exportMetrics = IsSet(configuration, OtlpEndpointKey) || IsSet(configuration, OtlpMetricsEndpointKey);
        SpanErrors.RedactRecordedExceptions();

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(application.Name, serviceVersion: application.Version)
                .AddAttributes([new("deployment.environment.name", application.Environment)])
                // Last, so OTEL_SERVICE_NAME and OTEL_RESOURCE_ATTRIBUTES can still override the values above.
                .AddEnvironmentVariableDetector())
            .WithTracing(tracing =>
            {
                tracing.AddAspNetCoreInstrumentation(ConfigureServerSpans)
                    .AddHttpClientInstrumentation(ConfigureClientSpans)
                    .AddSource(ApplicationSourceNames)
                    // First, so the exporter and every processor added later see the cleared status.
                    .AddProcessor(SpanErrors.CreateStatusRedaction());
                if (exportTraces)
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(ApplicationSourceNames);
                if (exportMetrics)
                {
                    metrics.AddOtlpExporter();
                }
            });
    }

    private static void ConfigureServerSpans(AspNetCoreTraceInstrumentationOptions options)
    {
        options.Filter = context => !HealthProbes.IsProbe(context.Request.Path);
        options.RecordException = false;
        // The query string can carry tokens (it is otherwise recorded with its values redacted, but its keys kept) and
        // the user agent is a header value.
        options.EnrichWithHttpRequest = (activity, _) =>
        {
            activity.SetTag("url.query", null);
            activity.SetTag("user_agent.original", null);
        };
    }

    private static void ConfigureClientSpans(HttpClientTraceInstrumentationOptions options)
    {
        options.RecordException = false;
        // The scheme, host, port and path of the target; never its user information or query string.
        options.EnrichWithHttpRequestMessage = (activity, request) =>
        {
            if (request.RequestUri is { IsAbsoluteUri: true } uri)
            {
                activity.SetTag("url.full", uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped));
            }
        };
    }

    private static bool IsSet(IConfiguration configuration, string key) => !string.IsNullOrWhiteSpace(configuration[key]);
}
