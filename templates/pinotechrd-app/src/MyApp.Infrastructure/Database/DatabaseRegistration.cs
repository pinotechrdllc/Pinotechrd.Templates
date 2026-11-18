using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace MyApp.Infrastructure.Database;

/// <summary>
/// The connection to PostgreSQL: one <see cref="NpgsqlDataSource"/> for the whole process (its connection pool), built
/// from the connection string <see cref="ConnectionStringKey"/>.
/// <para>
/// The connection string is a secret. It is read from the configuration when the data source is first needed, never
/// logged and never quoted in an error. A missing, empty or invalid value stops the host when it starts, with an
/// <see cref="OptionsValidationException"/> that names the setting and not its value. Registering the services reads
/// nothing and opens nothing, so the host can be built without a database (the build-time OpenAPI generation does).
/// </para>
/// <para>
/// Set on every connection, whatever the connection string says: <c>Include Error Detail</c> off, because error
/// details can carry row values into exceptions and logs, and <c>Persist Security Info</c> off, so the connection
/// string read back from a connection or the data source never holds the password. Set unless the connection string
/// names them: <c>Application Name</c> (the application name, shown in <c>pg_stat_activity</c>),
/// <c>Command Timeout</c> (<see cref="DefaultCommandTimeoutSeconds"/>) and <c>Maximum Pool Size</c>
/// (<see cref="DefaultMaximumPoolSize"/>). Everything else is Npgsql's default, overridable in the connection string:
/// pooling on, <c>Minimum Pool Size</c> 0, <c>Connection Idle Lifetime</c> 300 seconds, <c>Timeout</c> (to connect)
/// 15 seconds, <c>Cancellation Timeout</c> 2000 milliseconds. Npgsql writes no log events
/// (no logger factory is given to it: its debug events name the host, the database and the commands); its traces and
/// metrics are published under <see cref="TelemetryName"/>, without parameter values and without physical connection
/// opens.
/// </para>
/// </summary>
public static class DatabaseRegistration
{
    /// <summary>The name of the connection string, read with <c>GetConnectionString</c>.</summary>
    public const string ConnectionStringName = "Database";

    /// <summary>The configuration key of the connection string; the environment variable is <c>ConnectionStrings__Database</c>.</summary>
    public const string ConnectionStringKey = "ConnectionStrings:" + ConnectionStringName;

    /// <summary>
    /// The name of Npgsql's activity source and meter, to collect its spans and metrics. A command span records the
    /// statement text, never parameter values.
    /// </summary>
    public const string TelemetryName = "Npgsql";

    /// <summary>
    /// The command timeout, in seconds, when the connection string does not set <c>Command Timeout</c>: Npgsql's own
    /// default, written down so a request never waits on a statement without a limit chosen on purpose.
    /// </summary>
    public const int DefaultCommandTimeoutSeconds = 30;

    /// <summary>
    /// The largest pool of one process when the connection string does not set <c>Maximum Pool Size</c>, instead of
    /// Npgsql's 100, which alone fills PostgreSQL's default <c>max_connections</c> of 100: four instances at 20 use 80
    /// and leave room for the migrator, the reserved superuser slots and an operator.
    /// </summary>
    public const int DefaultMaximumPoolSize = 20;

    /// <summary>
    /// Registers the shared <see cref="NpgsqlDataSource"/> as a singleton, disposed with the host, the validation of
    /// <see cref="ConnectionStringKey"/> and <see cref="DatabaseHealthCheck"/> as a singleton.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="validateOnStart">
    /// Whether the connection string is validated when the host starts, so an invalid one stops it. Pass
    /// <see langword="false"/> only for a host that starts without configuration and never serves a request, such as
    /// the build-time OpenAPI document generation; the data source still refuses an invalid value when it is resolved.
    /// </param>
    /// <returns>The same service collection, for chaining.</returns>
    public static IServiceCollection AddDatabase(this IServiceCollection services, bool validateOnStart = true)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<DatabaseOptions>()
            .Configure<IConfiguration>((options, configuration) =>
                options.ConnectionString = configuration.GetConnectionString(ConnectionStringName));
        if (validateOnStart)
        {
            options.ValidateOnStart();
        }

        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>());
        services.TryAddSingleton(CreateDataSource);
        // A singleton, so concurrent and successive probes share one attempt (see DatabaseHealthCheck).
        services.TryAddSingleton<DatabaseHealthCheck>();
        return services;
    }

    private static NpgsqlDataSource CreateDataSource(IServiceProvider services)
    {
        // Validated: an invalid connection string throws here too, when something resolves the data source early.
        var options = services.GetRequiredService<IOptions<DatabaseOptions>>().Value;
        var applicationName = services.GetRequiredService<IHostEnvironment>().ApplicationName;

        var builder = new NpgsqlDataSourceBuilder(options.ConnectionString);
        var connection = builder.ConnectionStringBuilder;
        connection.IncludeErrorDetail = false;
        connection.PersistSecurityInfo = false;
        if (string.IsNullOrEmpty(connection.ApplicationName))
        {
            connection.ApplicationName = applicationName;
        }

        if (!Names(connection, "Command Timeout"))
        {
            connection.CommandTimeout = DefaultCommandTimeoutSeconds;
        }

        if (!Names(connection, "Maximum Pool Size"))
        {
            connection.MaxPoolSize = DefaultMaximumPoolSize;
        }

        // The pool name in metrics and spans; otherwise it is the connection string, with the host and the user name.
        builder.Name = applicationName;
        builder.ConfigureTracing(tracing => tracing
            .ConfigureCommandFilter(command => !DatabaseHealthCheck.IsProbeCommand(command))
            .EnablePhysicalOpenTracing(false));
        return builder.Build();
    }

    // Keys holds only the keywords the connection string names, under their canonical spelling, whichever synonym was
    // written (MaxPoolSize, maximum pool size...).
#pragma warning disable CA1841 // ContainsKey is true for every keyword Npgsql knows, named or not.
    private static bool Names(NpgsqlConnectionStringBuilder connection, string canonicalKeyword) =>
        connection.Keys.Contains(canonicalKeyword);
#pragma warning restore CA1841
}
