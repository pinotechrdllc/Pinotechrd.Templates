using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Domain;
using MyApp.BuildingBlocks.Infrastructure.Migrations;
using MyApp.Modules.Access.Application.Tenants;
using MyApp.Modules.Access.Infrastructure.Persistence.Catalog;
using MyApp.Modules.Access.Infrastructure.Seeding;
using Npgsql;

namespace MyApp.Migrator;

/// <summary>
/// The migrator command: reads its settings (command line, environment, user secrets in Development, appsettings),
/// validates them, migrates the shared catalog and then every tenant under one global lock, writes a report and returns
/// the exit code. The report has one line for each shared schema, one per tenant (its id and the migrations applied), one
/// for the plans seeded in the catalog once it is up to date (ADR 0013) and a summary; it never holds a connection string
/// or a password. With the <see cref="ProvisionCommand"/> command it
/// provisions one tenant instead (ADR 0011), once the catalog is up to date, with the same exit codes.
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

    /// <summary>
    /// The command that provisions one tenant: <c>provision --Provision:Slug=acme --Provision:Name=Acme
    /// --Provision:SubscriptionId=&lt;id&gt;</c>. Without a command the migrator migrates.
    /// </summary>
    public const string ProvisionCommand = "provision";

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

        // A first argument that is not a setting (--Key=value, /Key=value or Key=value) is a command, stripped before the
        // configuration reads the rest. Unknown commands are refused without quoting them.
        var provision = false;
        if (args.Length > 0 && IsCommand(args[0]))
        {
            if (!string.Equals(args[0], ProvisionCommand, StringComparison.Ordinal))
            {
                await report.WriteLineAsync($"Invalid settings: unknown command. The only command is '{ProvisionCommand}'; without one, the migrator migrates.")
                    .ConfigureAwait(false);
                return InvalidSettings;
            }

            provision = true;
            args = args[1..];
        }

        // A misplaced command must not silently become a full migration: the rest must be settings only.
        if (ArgumentsFailure(args, provision) is { } refused)
        {
            await report.WriteLineAsync($"Invalid settings: {refused}").ConfigureAwait(false);
            return InvalidSettings;
        }

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
        // Provision settings from any source (the environment's Provision__Slug included) belong to the provision command
        // only: without it they would be ignored and the migrator would migrate.
        if (!provision && builder.Configuration.GetSection(MigratorComposition.ProvisionSectionName).Exists())
        {
            await report.WriteLineAsync($"Invalid settings: the '{MigratorComposition.ProvisionSectionName}' settings are only read by the '{ProvisionCommand}' command, which must be the first argument.")
                .ConfigureAwait(false);
            return InvalidSettings;
        }

        builder.AddMigrations();
        if (provision)
        {
            builder.AddProvisioning();
        }

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
            if (provision)
            {
                var request = host.Services.GetRequiredService<IOptions<ProvisionOptions>>().Value.ToRequest();
                return await ProvisionAsync(host.Services, request, report, linked.Token).ConfigureAwait(false);
            }

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

        // The plans once the catalog is up to date, whatever the tenants did: they live in the catalog only.
        var plansSeeded = true;
        if (result.Shared.All(s => s.Succeeded))
        {
            try
            {
                plansSeeded = await SeedPlansAsync(services, report, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ReportCancelledAsync(report).ConfigureAwait(false);
                return Cancelled;
            }
        }

        var failedTenants = result.Tenants.Count(t => !t.Succeeded);
        var sharedDone = result.Shared.Count(s => s.Succeeded);
        var tenantsDone = result.Tenants.Count - failedTenants;
        await report.WriteLineAsync(Invariant(
                $"Done: {sharedDone} of {result.Shared.Count} shared schema(s) and {tenantsDone} of {result.Tenants.Count} tenant(s) up to date, {failedTenants} tenant(s) failed."))
            .ConfigureAwait(false);
        return result.Succeeded && plansSeeded ? Success : MigrationFailed;
    }

    // One report line: the plans and plan modules added, or the failure. A cancellation goes up as one.
    private static async Task<bool> SeedPlansAsync(IServiceProvider services, TextWriter report, CancellationToken cancellationToken)
    {
        CatalogSeedResult seeded;
        try
        {
            var scope = services.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                seeded = await scope.ServiceProvider.GetRequiredService<CatalogPlanSeeder>().SeedAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await report.WriteLineAsync($"Plans: failed. {Describe(exception)}").ConfigureAwait(false);
            return false;
        }

        await report.WriteLineAsync(Invariant($"Plans: {seeded.PlansAdded} plan(s) and {seeded.PlanModulesAdded} plan module(s) added."))
            .ConfigureAwait(false);
        return true;
    }

    // Provisions one tenant once the catalog is up to date. Not under the global lock: the tenant's own migration lock
    // keeps it apart from a concurrent migration of that tenant.
    private static async Task<int> ProvisionAsync(
        IServiceProvider services, ProvisionTenantRequest request, TextWriter report, CancellationToken cancellationToken)
    {
        Result<ProvisionedTenant> result;
        try
        {
            var scope = services.CreateAsyncScope();
            await using (scope.ConfigureAwait(false))
            {
                var catalog = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
                if ((await catalog.Database.GetPendingMigrationsAsync(cancellationToken).ConfigureAwait(false)).Any())
                {
                    await report.WriteLineAsync("Provisioning failed: the catalog is not up to date. Run the migrator without a command (migrate) first.")
                        .ConfigureAwait(false);
                    return MigrationFailed;
                }

                result = await scope.ServiceProvider.GetRequiredService<ITenantProvisioner>().ProvisionAsync(request, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            await report.WriteLineAsync($"Provisioning failed: {Describe(exception)}").ConfigureAwait(false);
            return MigrationFailed;
        }
        catch (OperationCanceledException)
        {
            await report.WriteLineAsync("Provisioning cancelled: what finished stays; run the same command again to complete it.").ConfigureAwait(false);
            return Cancelled;
        }

        if (result.IsFailure)
        {
            // Error messages never quote the request's values.
            await report.WriteLineAsync($"Provisioning failed: {result.Error.Code}: {result.Error.Message}").ConfigureAwait(false);
            return result.Error.Kind == ErrorKind.Validation ? InvalidSettings : MigrationFailed;
        }

        var tenant = result.Value;
        await report.WriteLineAsync(Invariant(
                $"Tenant {tenant.TenantId} ({tenant.Slug}): {(tenant.AlreadyProvisioned ? "already provisioned" : "provisioned")}."))
            .ConfigureAwait(false);
        return Success;
    }

    private static bool IsCommand(string argument) =>
        argument.Length > 0 && argument[0] is not '-' and not '/' && !argument.Contains('=', StringComparison.Ordinal);

    // Why the arguments after the command (or all of them, without one) are refused; null when they are settings only.
    // They are read as the command-line configuration reads them: --Key=value, /Key=value, Key=value, or --Key / /Key
    // followed by its value. Anything else would be ignored by it. No message quotes an argument.
    private static string? ArgumentsFailure(string[] args, bool provision)
    {
        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            if (string.Equals(argument, ProvisionCommand, StringComparison.OrdinalIgnoreCase))
            {
                return $"the '{ProvisionCommand}' command must be the first argument.";
            }

            var prefix = argument.StartsWith("--", StringComparison.Ordinal) ? 2 : argument.StartsWith('/') ? 1 : 0;
            if (prefix == 0 && (argument.Length == 0 || argument[0] == '-' || !argument.Contains('=', StringComparison.Ordinal)))
            {
                return "an argument is neither a command nor a setting; settings are --Key=value.";
            }

            var separator = argument.IndexOf('=', StringComparison.Ordinal);
            var key = separator < 0 ? argument[prefix..] : argument[prefix..separator];
            if (string.Equals(key, ProvisionCommand, StringComparison.OrdinalIgnoreCase))
            {
                return $"'{ProvisionCommand}' is a command, not a setting; it must be the first argument.";
            }

            if (!provision && IsProvisionSetting(key))
            {
                return $"the '{MigratorComposition.ProvisionSectionName}' settings are only read by the '{ProvisionCommand}' command, which must be the first argument.";
            }

            if (separator < 0)
            {
                // --Key value: the next argument is the value. A last --Key without one would be ignored.
                if (i + 1 >= args.Length)
                {
                    return "a setting has no value; settings are --Key=value.";
                }

                if (string.Equals(args[i + 1], ProvisionCommand, StringComparison.OrdinalIgnoreCase))
                {
                    return $"the '{ProvisionCommand}' command must be the first argument.";
                }

                i++;
            }
        }

        return null;
    }

    private static bool IsProvisionSetting(string key) =>
        key.StartsWith(MigratorComposition.ProvisionSectionName + ":", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith(MigratorComposition.ProvisionSectionName + "__", StringComparison.OrdinalIgnoreCase);

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
