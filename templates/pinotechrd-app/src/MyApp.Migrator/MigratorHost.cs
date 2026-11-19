using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using Npgsql;

namespace MyApp.Migrator;

/// <summary>
/// The migrator command: reads its settings (command line, environment, user secrets in Development, appsettings),
/// validates them, migrates the shared catalog and then every tenant under one global lock, writes a report and returns
/// the exit code. The report has one line for each shared schema, one per tenant (its id and the migrations applied) and
/// a summary; it never holds a connection string or a password.
/// </summary>
public static class MigratorHost
{
    /// <summary>Every shared schema and every tenant is up to date.</summary>
    public const int Success = 0;

    /// <summary>A shared schema or a tenant failed; the report names it.</summary>
    public const int MigrationFailed = 1;

    /// <summary>A setting is missing or invalid; nothing was migrated.</summary>
    public const int InvalidSettings = 2;

    /// <summary>
    /// The run was cancelled (Ctrl+C, a stop signal, or the caller's token): what finished stays applied, the locks are
    /// released, and running it again completes the rest. 128 + SIGINT, the shell's convention for an interrupted command.
    /// </summary>
    public const int Cancelled = 130;

    /// <summary>Runs the migrator.</summary>
    /// <param name="args">The command-line arguments, such as <c>--Migrator:MaxDegreeOfParallelism=2</c>.</param>
    /// <param name="report">Where the report is written.</param>
    /// <param name="cancellationToken">Stops the run, as does a stop signal to the process.</param>
    /// <returns>
    /// The exit code: <see cref="Success"/>, <see cref="MigrationFailed"/>, <see cref="InvalidSettings"/> or
    /// <see cref="Cancelled"/>.
    /// </returns>
    public static async Task<int> RunAsync(string[] args, TextWriter report, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(report);

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            Args = args,
            // User secrets and the pool's application name follow this assembly, not the process that hosts it.
            ApplicationName = typeof(MigratorHost).Assembly.GetName().Name,
        });
        // Only warnings and errors, on standard error: the report is the output. Left out: EF's command log (the first
        // run's read of a history table that does not exist yet is logged as an error) and the host's start failure, which
        // the report already gives as invalid settings.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(console => console.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.EntityFrameworkCore.Database.Command", LogLevel.None);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting", LogLevel.None);
        builder.Services.Configure<ConsoleLifetimeOptions>(o => o.SuppressStatusMessages = true);
        builder.AddMigrations();

        using var host = builder.Build();
        try
        {
            // Starting the host runs the start-time validation of every setting.
            await host.StartAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SettingsFailures(exception) is { Count: > 0 } failures)
        {
            foreach (var failure in failures)
            {
                await report.WriteLineAsync($"Invalid settings: {failure}").ConfigureAwait(false);
            }

            return InvalidSettings;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReportCancelledAsync(report).ConfigureAwait(false);
            return Cancelled;
        }

        try
        {
            var stopping = host.Services.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping);
            var parallelism = host.Services.GetRequiredService<IOptions<MigratorOptions>>().Value.MaxDegreeOfParallelism;
            return await MigrateAsync(host.Services, parallelism, report, linked.Token).ConfigureAwait(false);
        }
        finally
        {
            await host.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<int> MigrateAsync(IServiceProvider services, int parallelism, TextWriter report, CancellationToken cancellationToken)
    {
        DatabaseMigrationResult result;
        try
        {
            result = await services.GetRequiredService<IDatabaseMigrator>().MigrateAsync(parallelism, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await report.WriteLineAsync($"Migration failed: {Describe(exception)}").ConfigureAwait(false);
            return MigrationFailed;
        }
        catch (OperationCanceledException)
        {
            // Only a cancellation of this run's token reaches here; the migrator released its locks before it was thrown.
            await ReportCancelledAsync(report).ConfigureAwait(false);
            return Cancelled;
        }

        foreach (var shared in result.Shared)
        {
            await report.WriteLineAsync(shared.Succeeded
                ? Invariant($"Schema {shared.Schema}: {shared.AppliedMigrations.Count} migration(s) applied.")
                : $"Schema {shared.Schema}: failed, no tenant migrated. {Describe(shared.Error!)}").ConfigureAwait(false);
        }

        foreach (var tenant in result.Tenants)
        {
            var applied = tenant.Modules.Sum(m => m.AppliedMigrations.Count);
            await report.WriteLineAsync(tenant.Succeeded
                ? Invariant($"Tenant {tenant.TenantId}: {applied} migration(s) applied.")
                : Invariant($"Tenant {tenant.TenantId}: failed after {applied} migration(s). {Describe(tenant.Error!)}")).ConfigureAwait(false);
        }

        var failedTenants = result.Tenants.Count(t => !t.Succeeded);
        var sharedDone = result.Shared.Count(s => s.Succeeded);
        var tenantsDone = result.Tenants.Count - failedTenants;
        await report.WriteLineAsync(Invariant(
                $"Done: {sharedDone} of {result.Shared.Count} shared schema(s) and {tenantsDone} of {result.Tenants.Count} tenant(s) up to date, {failedTenants} tenant(s) failed."))
            .ConfigureAwait(false);
        return result.Succeeded ? Success : MigrationFailed;
    }

    private static Task ReportCancelledAsync(TextWriter report) =>
        report.WriteLineAsync("Migration cancelled: what finished stays applied; run the migrator again to complete the rest.");

    // The messages of the settings that failed their start-time validation; empty for any other failure.
    private static List<string> SettingsFailures(Exception exception) => exception switch
    {
        OptionsValidationException validation => [.. validation.Failures],
        AggregateException aggregate when aggregate.InnerExceptions.All(e => e is OptionsValidationException) =>
            [.. aggregate.InnerExceptions.Cast<OptionsValidationException>().SelectMany(e => e.Failures)],
        // A value the configuration binder cannot convert (a parallelism that is not a number): its message names the key.
        InvalidOperationException { InnerException: FormatException or OverflowException or ArgumentException } binding => [binding.Message],
        _ => [],
    };

    // A server error gives its SQLSTATE and text; a connection error only its type, because Npgsql's messages name the
    // host and the user. Nothing here can hold the connection string.
    private static string Describe(Exception exception) => exception switch
    {
        PostgresException postgres => $"{postgres.SqlState}: {postgres.MessageText}",
        NpgsqlException => exception.GetType().Name,
        _ => $"{exception.GetType().Name}: {exception.Message}",
    };

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);
}
