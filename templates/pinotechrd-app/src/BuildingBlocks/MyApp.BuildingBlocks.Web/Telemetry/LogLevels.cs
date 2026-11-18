using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace MyApp.BuildingBlocks.Web.Telemetry;

/// <summary>
/// The minimum log levels, read from the standard <c>Logging:LogLevel</c> section: <c>Default</c> sets the level of
/// every category and any other key overrides it for the categories that start with that name (the most specific one
/// wins). Values are the <see cref="LogLevel"/> names (<c>Trace</c>, <c>Debug</c>, <c>Information</c>, <c>Warning</c>,
/// <c>Error</c>, <c>Critical</c>, <c>None</c>). Provider sections such as <c>Logging:Console</c> and wildcard category
/// names are not read: there is only one output. The categories of
/// <see cref="TelemetryRegistration.PinnedLogCategories"/> stay at <c>Warning</c> unless they are named themselves.
/// </summary>
internal sealed class LogLevels
{
    /// <summary>The configuration section the levels are read from.</summary>
    public const string SectionName = "Logging:LogLevel";

    private const string DefaultKey = "Default";

    private readonly LogEventLevel _default;
    private readonly IReadOnlyList<KeyValuePair<string, LogEventLevel>> _overrides;

    private LogLevels(LogEventLevel defaultLevel, IReadOnlyList<KeyValuePair<string, LogEventLevel>> overrides)
    {
        _default = defaultLevel;
        _overrides = overrides;
    }

    /// <summary>
    /// Reads the levels. With no <c>Default</c> the level is <c>Information</c>, as in the platform. An unknown level
    /// name stops the host with an exception that names the setting.
    /// </summary>
    public static LogLevels From(IConfiguration configuration)
    {
        var defaultLevel = LogEventLevel.Information;
        var overrides = new List<KeyValuePair<string, LogEventLevel>>();
        foreach (var setting in configuration.GetSection(SectionName).GetChildren())
        {
            var level = Parse(setting);
            if (string.Equals(setting.Key, DefaultKey, StringComparison.OrdinalIgnoreCase))
            {
                defaultLevel = level;
            }
            else
            {
                overrides.Add(new(setting.Key, level));
            }
        }

        // A pinned category stays at Warning when only a parent (Default, Microsoft.AspNetCore) is raised; naming it is the
        // operator's deliberate choice. A more specific name, such as one of its classes, wins over the pin as usual.
        foreach (var pinned in TelemetryRegistration.PinnedLogCategories)
        {
            if (!overrides.Exists(o => string.Equals(o.Key, pinned, StringComparison.OrdinalIgnoreCase)))
            {
                overrides.Add(new(pinned, LogEventLevel.Warning));
            }
        }

        return new(defaultLevel, overrides);
    }

    /// <summary>Applies the levels to a logger configuration.</summary>
    public LoggerConfiguration ApplyTo(LoggerMinimumLevelConfiguration minimumLevel)
    {
        var configuration = minimumLevel.Is(_default);
        foreach (var (category, level) in _overrides)
        {
            configuration = configuration.MinimumLevel.Override(category, level);
        }

        return configuration;
    }

    private static LogEventLevel Parse(IConfigurationSection setting)
    {
        if (!Enum.TryParse<LogLevel>(setting.Value, ignoreCase: true, out var level) || !Enum.IsDefined(level))
        {
            throw new InvalidOperationException(
                $"The log level '{setting.Value}' of the setting '{setting.Path}' is not valid. Use Trace, Debug, Information, Warning, Error, Critical or None.");
        }

        return level switch
        {
            LogLevel.Trace => LogEventLevel.Verbose,
            LogLevel.Debug => LogEventLevel.Debug,
            LogLevel.Information => LogEventLevel.Information,
            LogLevel.Warning => LogEventLevel.Warning,
            LogLevel.Error => LogEventLevel.Error,
            LogLevel.Critical => LogEventLevel.Fatal,
            _ => LevelAlias.Off,
        };
    }
}
