using System.Data.Common;
using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using Npgsql;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// Runs <see cref="RefreshTokenPurger"/> on a schedule: after a random wait of up to
/// <see cref="RefreshTokenPurgeOptions.InitialDelay"/>, one round, then a wait of
/// <see cref="RefreshTokenPurgeOptions.Interval"/> varied by <see cref="RefreshTokenPurgeOptions.Jitter"/>, and so on until
/// the host stops. It does nothing when <see cref="RefreshTokenPurgeOptions.Enabled"/> is false. Every instance runs it; the
/// purger's advisory lock lets one purge a tenant at a time.
/// <para>
/// A round has the <see cref="ITenantJobRunner"/> visit the in-service tenants (Active and Suspended; Provisioning ones have
/// no schema yet, Cancelled ones are left alone) one after another, each in its own scope with the tenant set. A tenant whose schema or role is missing, whose database call fails
/// or times out, is logged (tenant id, exception type, SQLSTATE; never a message) and skipped; the round goes on. A failing
/// tenant directory ends the round; the next one retries. Nothing but a stop of the host ends the loop.
/// </para>
/// <para>
/// The tenant list is read once, at the start of each round. A tenant whose status changes during a round (cancelled, say)
/// may still be purged in that round; only families past their expiry or revocation plus the grace are deleted.
/// </para>
/// <para>
/// A tenant whose purge found its lock held for <see cref="SkippedRoundsWarning"/> rounds in a row is logged as a warning:
/// any role with SQL access can hold the lock (see <see cref="RefreshTokenPurger"/>).
/// </para>
/// </summary>
/// <param name="jobRunner">Visits each in-service tenant with the purge.</param>
/// <param name="options">The schedule.</param>
/// <param name="logger">The logger of the rounds.</param>
/// <param name="timeProvider">The clock of the waits; the system's when none is registered.</param>
public sealed partial class RefreshTokenPurgeService(
    ITenantJobRunner jobRunner,
    IOptions<RefreshTokenPurgeOptions> options,
    ILogger<RefreshTokenPurgeService> logger,
    TimeProvider? timeProvider = null) : BackgroundService
{
    /// <summary>How many consecutive rounds a tenant's purge may find its lock held before a warning is logged (and again every that many).</summary>
    public const int SkippedRoundsWarning = 24;

    private readonly TimeProvider _clock = timeProvider ?? TimeProvider.System;
    private readonly Dictionary<Guid, int> _skippedRounds = [];

    /// <summary>Runs one round: every purgeable tenant once. Throws only <see cref="OperationCanceledException"/>, when the token is cancelled. Not thread-safe: the hosted loop calls it sequentially, and so must any other caller.</summary>
    /// <param name="cancellationToken">Stops the round.</param>
    /// <returns>The counts of the round.</returns>
    public async Task<RefreshTokenPurgeRound> RunRoundAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<TenantJobOutcome<RefreshTokenPurgeResult>> outcomes;
        try
        {
            outcomes = await jobRunner.ForEachTenantAsync(
                (provider, token) => provider.GetRequiredService<RefreshTokenPurger>().PurgeAsync(token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogCatalogFailed(logger, exception.GetType().Name, SqlStateOf(exception));
            return new RefreshTokenPurgeRound(0, 0, 0);
        }

        var deleted = 0;
        var failed = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.Error is { } exception)
            {
                failed++;
                if (exception is DbException or InvalidOperationException or TimeoutException or ArgumentException)
                {
                    // A missing schema or role (3F000, 42P01, 42501, 28000, 22023), the purger's schema guard, a timeout, a database that is down.
                    LogTenantSkipped(logger, outcome.TenantId, exception.GetType().Name, SqlStateOf(exception));
                }
                else
                {
                    LogTenantFailed(logger, outcome.TenantId, exception.GetType().Name);
                }
            }
            else if (outcome.Result is { } result)
            {
                deleted += result.Deleted;
                CountSkips(outcome.TenantId, result.LockSkipped);
            }
        }

        // The counts of tenants that left the list are forgotten.
        var listed = outcomes.Select(o => o.TenantId).ToHashSet();
        foreach (var gone in _skippedRounds.Keys.Where(id => !listed.Contains(id)).ToList())
        {
            _skippedRounds.Remove(gone);
        }

        LogRound(logger, outcomes.Count, deleted, failed);
        return new RefreshTokenPurgeRound(outcomes.Count, deleted, failed);
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            return;
        }

        try
        {
            await Task.Delay(settings.InitialDelay * NextFraction(), _clock, stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await RunRoundAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    // Not expected (RunRoundAsync handles each tenant); an unhandled exception here would stop the host.
                    LogRoundFailed(logger, exception.GetType().Name);
                }

                var factor = 1 + (settings.Jitter * ((2 * NextFraction()) - 1));
                await Task.Delay(settings.Interval * factor, _clock, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // The host is stopping.
        }
    }

    private void CountSkips(Guid tenantId, bool lockSkipped)
    {
        if (!lockSkipped)
        {
            _skippedRounds.Remove(tenantId);
            return;
        }

        var count = _skippedRounds.GetValueOrDefault(tenantId) + 1;
        _skippedRounds[tenantId] = count;
        if (count % SkippedRoundsWarning == 0)
        {
            LogLockHeld(logger, tenantId, count);
        }
    }

    // A uniform value in [0, 1]: scheduling only, taken from the operating system's generator so no analyzer needs silencing.
    private static double NextFraction() => RandomNumberGenerator.GetInt32(0, 1_000_001) / 1_000_000d;

    private static string SqlStateOf(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException)?.SqlState ?? "none";

    [LoggerMessage(Level = LogLevel.Warning, Message = "The refresh token purge could not list the tenants ({ExceptionType}, SQLSTATE {SqlState}); the next round tries again.")]
    private static partial void LogCatalogFailed(ILogger logger, string exceptionType, string sqlState);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The refresh token purge skipped tenant {TenantId} ({ExceptionType}, SQLSTATE {SqlState}).")]
    private static partial void LogTenantSkipped(ILogger logger, Guid tenantId, string exceptionType, string sqlState);

    [LoggerMessage(Level = LogLevel.Error, Message = "The refresh token purge of tenant {TenantId} failed unexpectedly ({ExceptionType}).")]
    private static partial void LogTenantFailed(ILogger logger, Guid tenantId, string exceptionType);

    [LoggerMessage(Level = LogLevel.Error, Message = "A refresh token purge round failed unexpectedly ({ExceptionType}).")]
    private static partial void LogRoundFailed(ILogger logger, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The refresh token purge of tenant {TenantId} found its lock held in {Rounds} rounds in a row; another session may be holding it.")]
    private static partial void LogLockHeld(ILogger logger, Guid tenantId, int rounds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Refresh token purge round: {Tenants} tenants visited, {Deleted} families deleted, {Failed} tenants failed.")]
    private static partial void LogRound(ILogger logger, int tenants, int deleted, int failed);
}

/// <summary>The counts of one round of <see cref="RefreshTokenPurgeService"/>.</summary>
/// <param name="TenantsVisited">How many tenants the catalog listed (each was tried).</param>
/// <param name="FamiliesDeleted">How many refresh token families were deleted in all.</param>
/// <param name="TenantsFailed">How many tenants were skipped because of an error.</param>
public sealed record RefreshTokenPurgeRound(int TenantsVisited, int FamiliesDeleted, int TenantsFailed);
