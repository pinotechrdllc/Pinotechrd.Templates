using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Database;

/// <summary>
/// Reports whether PostgreSQL answers: it takes a connection from the shared <see cref="NpgsqlDataSource"/> and runs
/// <see cref="Query"/>. Registered as a singleton by <see cref="DatabaseRegistration.AddDatabase"/>.
/// <para>
/// It answers by the registration's timeout whatever the server does. Npgsql does not stop for a cancellation while
/// it opens a physical connection (the SSL request and the login wait for the server's first reply until the
/// connection string's <c>Timeout</c>, 15 seconds by default), and a cancelled query waits up to
/// <c>Cancellation Timeout</c> (2 seconds by default) for the server to confirm before Npgsql breaks the connection. So
/// the attempt runs on its own, cancelled at the registration's timeout, and the check stops waiting for it at that
/// same moment and reports a failure. An attempt that has not finished keeps its pool slot until Npgsql gives up (at
/// most <c>Timeout</c>, or <c>Cancellation Timeout</c> after the deadline for a query) and then releases it; checks made
/// meanwhile wait on that same attempt instead of starting another, so the probe never holds more than one slot.
/// </para>
/// <para>
/// A failure is reported with a fixed description and without the exception, because Npgsql's messages name the
/// host, the port and the user; the description holds only the exception type and, for a server error, its SQLSTATE
/// code. The query produces no span (see <see cref="DatabaseRegistration"/>): it runs as a command of a type private
/// to this class, and the tracing filter skips that type, never a statement text, so an application query with the
/// same text as <see cref="Query"/> is still traced.
/// </para>
/// <para>
/// It uses the data source directly, not a tenant context, so it needs no tenant and sees no tenant's schema.
/// </para>
/// </summary>
/// <param name="dataSource">The shared data source.</param>
public sealed class DatabaseHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    /// <summary>The name of the check in the health report.</summary>
    public const string Name = "database";

    /// <summary>The statement the check runs.</summary>
    public const string Query = "SELECT 1";

    private const string TimedOut = "The database did not answer before the health check timed out.";

    private readonly Lock _gate = new();
    private Task<Exception?>? _attempt;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var attempt = CurrentOrNewAttempt(context.Registration.Timeout);
        try
        {
            var failure = await attempt.WaitAsync(cancellationToken).ConfigureAwait(false);
            return failure is null
                ? HealthCheckResult.Healthy()
                : new HealthCheckResult(context.Registration.FailureStatus, Describe(failure));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The attempt is left to finish on its own; it never throws, so nothing goes unobserved.
            return new HealthCheckResult(context.Registration.FailureStatus, TimedOut);
        }
    }

    /// <summary>Whether <paramref name="command"/> is the probe's own command, which is not traced.</summary>
    internal static bool IsProbeCommand(NpgsqlCommand command) => command is ProbeCommand;

    private Task<Exception?> CurrentOrNewAttempt(TimeSpan timeout)
    {
        lock (_gate)
        {
            if (_attempt is not { IsCompleted: false })
            {
                _attempt = Task.Run(() => QueryAsync(timeout));
            }

            return _attempt;
        }
    }

    // Returns the failure instead of throwing it.
    private async Task<Exception?> QueryAsync(TimeSpan timeout)
    {
        using var deadline = timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan ? new CancellationTokenSource(timeout) : null;
        try
        {
            var token = deadline?.Token ?? CancellationToken.None;
            await using var connection = await dataSource.OpenConnectionAsync(token).ConfigureAwait(false);
            await using var command = new ProbeCommand(connection);
            await command.ExecuteScalarAsync(token).ConfigureAwait(false);
            return null;
        }
#pragma warning disable CA1031 // Any failure means the database is not usable; the exception must not reach the report.
        catch (Exception exception)
#pragma warning restore CA1031
        {
            return exception;
        }
    }

    // The marker of the probe's command for the tracing filter: no other code can create one.
    private sealed class ProbeCommand(NpgsqlConnection connection) : NpgsqlCommand(Query, connection);

    private static string Describe(Exception exception)
    {
        if (exception is OperationCanceledException)
        {
            return TimedOut;
        }

        var code = exception is PostgresException postgres ? $", SQLSTATE {postgres.SqlState}" : string.Empty;
        return $"The database did not answer ({exception.GetType().Name}{code}).";
    }
}
