using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// Deletes the refresh token families of the tenant of the scope that ended or were revoked more than
/// <see cref="RefreshTokenPurgeOptions.GracePeriod"/> ago; their tokens go with them (the foreign key cascades). The caller
/// initializes the tenant of the scope first; the SQL uses unqualified table names on the tenant connection of the context
/// (<c>OpenTenantConnectionAsync</c>), so the tenant's role and <c>search_path</c> scope it.
/// <list type="bullet">
/// <item>At most <see cref="RefreshTokenPurgeOptions.MaxBatchesPerTenant"/> batches of
/// <see cref="RefreshTokenPurgeOptions.BatchSize"/> families, each in its own transaction with a statement timeout and a
/// lock timeout.</item>
/// <item>A transaction-level advisory lock per tenant schema lets one instance purge a tenant at a time; when another holds
/// it, the purge stops without waiting. The key is built from the schema name of the current tenant, and the batch is
/// refused (<see cref="InvalidOperationException"/>, before any delete) unless <c>refresh_token_families</c> resolves, through
/// the connection's <c>search_path</c>, to a table in that schema. This also catches a <c>pg_temp</c> table that shadows it,
/// whether <c>pg_temp</c> is named in the <c>search_path</c> or searched implicitly.</item>
/// <item>Family rows locked by another transaction are skipped (<c>FOR UPDATE SKIP LOCKED</c>). The token rows the delete
/// cascades to are not: a locked token row makes the batch wait up to
/// <see cref="RefreshTokenPurgeOptions.LockTimeout"/>, then the batch rolls back and the purge of the tenant ends
/// with what was committed so far (logged as a warning). A statement cancelled on the server for another reason, such as a
/// DBA's <c>pg_cancel_backend</c> (57014) while the token is not cancelled, is treated like a timeout. A batch cancelled
/// through the token rolls back and throws <see cref="OperationCanceledException"/>.</item>
/// </list>
/// Advisory locks are database-wide and need no privilege, so any role with SQL access can hold a tenant's purge lock and
/// delay its purge. This is accepted (defence in depth, not a security boundary); the service that schedules the purge logs
/// repeated skips.
/// </summary>
/// <param name="context">The tenant context of the scope.</param>
/// <param name="currentTenant">The tenant of the scope.</param>
/// <param name="options">The batch settings.</param>
/// <param name="logger">The logger of the result.</param>
public sealed partial class RefreshTokenPurger(
    AccessDbContext context,
    ICurrentTenant currentTenant,
    IOptions<RefreshTokenPurgeOptions> options,
    ILogger<RefreshTokenPurger> logger)
{
    private const string TimeoutSql =
        "SELECT pg_catalog.set_config('statement_timeout', $1, true), pg_catalog.set_config('lock_timeout', $2, true)";

    // The key comes from the tenant's schema name computed in code ($1); the second column proves the unqualified table name
    // resolves into that schema (null, when it resolves nowhere, counts as not bound).
    private const string LockSql = """
        SELECT pg_catalog.pg_try_advisory_xact_lock(pg_catalog.hashtextextended('refresh-token-purge:' || $1, 0)),
               (SELECT n.nspname FROM pg_catalog.pg_class c
                JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE c.oid = pg_catalog.to_regclass('refresh_token_families')) = $1
        """;

    private const string DeleteSql = """
        WITH doomed AS (
            SELECT id FROM refresh_token_families
            WHERE absolute_expires_at < pg_catalog.now() - $1
               OR (revoked_at IS NOT NULL AND revoked_at < pg_catalog.now() - $1)
            ORDER BY id
            LIMIT $2
            FOR UPDATE SKIP LOCKED
        )
        DELETE FROM refresh_token_families AS f USING doomed AS d WHERE f.id = d.id
        """;

    /// <summary>Purges the tenant of the scope, batch by batch.</summary>
    /// <param name="cancellationToken">Cancels the purge; the batch in flight rolls back.</param>
    /// <returns>How many families were deleted (their tokens are not counted).</returns>
    /// <exception cref="InvalidOperationException">The scope has no current tenant.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled.</exception>
    public async Task<int> PurgeCurrentTenantAsync(CancellationToken cancellationToken = default)
    {
        var settings = options.Value;
        var connection = await context.Database.OpenTenantConnectionAsync(cancellationToken).ConfigureAwait(false);
        var timeout = Milliseconds(settings.StatementTimeout);
        var lockTimeout = Milliseconds(settings.LockTimeout);
        var schema = TenantSchemaName.For(currentTenant.TenantId);

        var total = 0;
        for (var batch = 0; batch < settings.MaxBatchesPerTenant; batch++)
        {
            int? deleted;
            try
            {
                deleted = await PurgeBatchAsync(connection, settings, schema, timeout, lockTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (
                exception.SqlState is PostgresErrorCodes.LockNotAvailable or PostgresErrorCodes.QueryCanceled)
            {
                // Npgsql may surface the server's cancel (57014) of our own token as a PostgresException: report it as a cancellation.
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("The refresh token purge was cancelled.", exception, cancellationToken);
                }

                // A locked token row or a slow statement: the batch rolled back; the rest waits for the next purge.
                LogBatchAbandoned(logger, currentTenant.TenantId, exception.SqlState, total);
                break;
            }

            if (deleted is null)
            {
                break;
            }

            total += deleted.Value;
            if (deleted.Value < settings.BatchSize)
            {
                break;
            }
        }

        if (total > 0)
        {
            LogPurged(logger, currentTenant.TenantId, total);
        }

        return total;
    }

    // Null when another instance holds the tenant's lock.
    private async Task<int?> PurgeBatchAsync(
        NpgsqlConnection connection,
        RefreshTokenPurgeOptions settings,
        string schema,
        string statementTimeoutMilliseconds,
        string lockTimeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var transaction = await context.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using (transaction.ConfigureAwait(false))
        {
            var dbTransaction = (NpgsqlTransaction)transaction.GetDbTransaction();

            var timeout = new NpgsqlCommand(TimeoutSql, connection, dbTransaction)
            {
                Parameters =
                {
                    new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = statementTimeoutMilliseconds },
                    new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = lockTimeoutMilliseconds },
                },
            };
            await using (timeout.ConfigureAwait(false))
            {
                await timeout.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            var advisory = new NpgsqlCommand(LockSql, connection, dbTransaction)
            {
                Parameters = { new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = schema } },
            };
            await using (advisory.ConfigureAwait(false))
            {
                var reader = await advisory.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                bool acquired;
                bool bound;
                await using (reader.ConfigureAwait(false))
                {
                    await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                    acquired = reader.GetBoolean(0);
                    bound = !await reader.IsDBNullAsync(1, cancellationToken).ConfigureAwait(false) && reader.GetBoolean(1);
                }

                if (!bound)
                {
                    throw new InvalidOperationException("The tenant connection is not bound to the current tenant's schema (refresh_token_families does not resolve into it).");
                }

                if (!acquired)
                {
                    return null;
                }
            }

            var delete = new NpgsqlCommand(DeleteSql, connection, dbTransaction)
            {
                Parameters =
                {
                    new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Interval, Value = ToMicrosecondInterval(settings.GracePeriod) },
                    new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Integer, Value = settings.BatchSize },
                },
            };
            int deleted;
            await using (delete.ConfigureAwait(false))
            {
                deleted = await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return deleted;
        }
    }

    private static string Milliseconds(TimeSpan value) =>
        Math.Ceiling(value.TotalMilliseconds).ToString("0", CultureInfo.InvariantCulture);

    // Microseconds only (no days, no months): a day is 23 or 25 hours in a session time zone with daylight saving.
    private static NpgsqlInterval ToMicrosecondInterval(TimeSpan value) => new(0, 0, value.Ticks / 10);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A refresh token purge batch of tenant {TenantId} was abandoned (SQLSTATE {SqlState}); {Count} families were purged before it.")]
    private static partial void LogBatchAbandoned(ILogger logger, Guid tenantId, string sqlState, int count);

    [LoggerMessage(Level = LogLevel.Information, Message = "Purged {Count} expired or revoked refresh token families in tenant {TenantId}.")]
    private static partial void LogPurged(ILogger logger, Guid tenantId, int count);
}
