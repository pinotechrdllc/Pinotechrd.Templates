using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Application.Tenancy;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Modules.Access.Infrastructure.Persistence;
using Npgsql;
using NpgsqlTypes;
using Pinotechrd.Security.Interfaces;

namespace MyApp.Modules.Access.Infrastructure.Security;

/// <summary>
/// The <see cref="IRefreshTokenStore"/> of the Access module, in the tenant's <c>refresh_token_families</c> and
/// <c>refresh_tokens</c> tables (<see cref="AccessDbContext"/>), scoped like the context. It replaces the library's
/// in-memory store, so tokens survive a restart and every instance shares them.
/// <list type="bullet">
/// <item>Only the SHA-256 of a token is stored. The tables have no tenant column: the scope's schema holds them, so a token
/// of another tenant is not found.</item>
/// <item>Every expiry is decided by the database clock (<c>now()</c>). A family's <c>absolute_expires_at</c> is set once,
/// when its first token is saved (<see cref="RefreshTokenOptions.AbsoluteLifetime"/>), and caps every token of it.</item>
/// <item><see cref="ConsumeAsync"/> is one <c>UPDATE ... RETURNING</c>: of two concurrent calls with one token exactly one
/// succeeds. When it updates nothing, a second query classifies why in the order of <see cref="IRefreshTokenStore"/>,
/// and a reused token revokes its family and is logged as a warning (the tenant's id only, never the token).</item>
/// <item><see cref="SaveAsync"/> is one <c>INSERT ... SELECT</c> that creates the family on first use. A token saved
/// into a revoked family is stored revoked; one saved while a revocation commits is still refused by the family's state
/// when consumed.</item>
/// <item><see cref="RevokeAsync"/> revokes the token's whole family (a logout ends that session);
/// <see cref="RevokeAllForUserAsync"/> every family of the user in this tenant, the only place the user exists.</item>
/// </list>
/// The SQL takes every value as a parameter and runs on the tenant connection of the context
/// (<c>OpenTenantConnectionAsync</c>), whose <c>search_path</c> finds the tables.
/// </summary>
/// <param name="context">The tenant context of the scope.</param>
/// <param name="currentTenant">The tenant of the scope, the one whose schema the context reads.</param>
/// <param name="options">The absolute lifetime of a family.</param>
/// <param name="logger">The logger of a reused token.</param>
public sealed partial class DatabaseRefreshTokenStore(
    AccessDbContext context,
    ICurrentTenant currentTenant,
    IOptions<RefreshTokenOptions> options,
    ILogger<DatabaseRefreshTokenStore> logger) : IRefreshTokenStore
{
    private const string SaveSql = """
        WITH created AS (
            INSERT INTO refresh_token_families (id, user_id, created_at, absolute_expires_at, revoked_at)
            VALUES ($2, $3, now(), now() + $7, NULL)
            ON CONFLICT (id) DO NOTHING
            RETURNING id, user_id, absolute_expires_at, revoked_at
        ), family AS (
            SELECT id, user_id, absolute_expires_at, revoked_at FROM created
            UNION ALL
            SELECT id, user_id, absolute_expires_at, revoked_at FROM refresh_token_families WHERE id = $2
        )
        INSERT INTO refresh_tokens (id, family_id, token_hash, user_id, security_stamp, created_at, expires_at, consumed_at, revoked_at)
        SELECT $1, f.id, $4, $3, $5, now(), least($6, f.absolute_expires_at), NULL,
               CASE WHEN f.revoked_at IS NULL THEN NULL ELSE now() END
        FROM family AS f
        WHERE f.user_id = $3
        LIMIT 1
        """;

    // The consume itself: zero rows means not consumed, and the state is classified afterwards.
    private const string ConsumeSql = """
        UPDATE refresh_tokens AS t
        SET consumed_at = now()
        FROM refresh_token_families AS f
        WHERE t.token_hash = $1
          AND f.id = t.family_id
          AND t.consumed_at IS NULL
          AND t.revoked_at IS NULL
          AND t.expires_at > now()
          AND f.revoked_at IS NULL
          AND f.absolute_expires_at > now()
        RETURNING t.user_id, t.family_id, t.security_stamp
        """;

    private const string StateSql = """
        SELECT t.user_id, t.family_id,
               f.revoked_at IS NOT NULL,
               t.consumed_at IS NOT NULL,
               t.revoked_at IS NOT NULL,
               t.expires_at <= now() OR f.absolute_expires_at <= now()
        FROM refresh_tokens AS t
        JOIN refresh_token_families AS f ON f.id = t.family_id
        WHERE t.token_hash = $1
        """;

    private const string RevokeFamilySql =
        "UPDATE refresh_token_families SET revoked_at = now() WHERE id = $1 AND revoked_at IS NULL";

    private const string RevokeTokenFamilySql = """
        UPDATE refresh_token_families SET revoked_at = now()
        WHERE revoked_at IS NULL AND id = (SELECT family_id FROM refresh_tokens WHERE token_hash = $1)
        """;

    private const string RevokeUserSql =
        "UPDATE refresh_token_families SET revoked_at = now() WHERE user_id = $1 AND revoked_at IS NULL";

    /// <inheritdoc />
    /// <exception cref="ArgumentException">A user or family identifier that is not a GUID.</exception>
    /// <exception cref="InvalidOperationException">The token is already stored, the tenant is not the scope's, or the
    /// family belongs to another user.</exception>
    public async Task SaveAsync(
        string userId,
        string tenantId,
        string refreshToken,
        DateTime expiresAtUtc,
        string? familyId = null,
        string? securityStamp = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(refreshToken);
        var user = ParseId(userId, nameof(userId));
        var family = familyId is null ? Guid.CreateVersion7() : ParseId(familyId, nameof(familyId));
        // The schema decides the tenant: a token for another one cannot be stored here.
        if (!IsCurrentTenant(tenantId))
        {
            throw new InvalidOperationException("A refresh token can only be saved for the tenant of the scope.");
        }

        var connection = await context.Database.OpenTenantConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(SaveSql, connection)
        {
            Parameters =
            {
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = Guid.CreateVersion7() },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = family },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = user },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Bytea, Value = Hash(refreshToken) },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Text, Value = (object?)securityStamp ?? DBNull.Value },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.TimestampTz, Value = AsUtc(expiresAtUtc) },
                new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Interval, Value = new NpgsqlInterval(0, 0, options.Value.AbsoluteLifetime.Ticks / 10) },
            },
        };

        int inserted;
        try
        {
            inserted = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException exception) when (exception is
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: AccessDbContext.RefreshTokenHashIndex })
        {
            // Refused, never overwritten: resetting a stored token's state would undo a consume or a revocation.
            throw new InvalidOperationException("This refresh token is already stored.", exception);
        }

        if (inserted != 1)
        {
            throw new InvalidOperationException("The refresh token family belongs to another user.");
        }
    }

    /// <inheritdoc />
    public async Task<RefreshTokenValidationResult> ValidateAsync(string refreshToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return new RefreshTokenValidationResult(false, Reason: "Refresh token not found");
        }

        var state = await ReadStateAsync(Hash(refreshToken), cancellationToken).ConfigureAwait(false);
        return state switch
        {
            null => new RefreshTokenValidationResult(false, Reason: "Refresh token not found"),
            { FamilyRevoked: true } or { TokenRevoked: true } => new RefreshTokenValidationResult(false, Reason: "Refresh token revoked"),
            { Consumed: true } => new RefreshTokenValidationResult(false, Reason: "Refresh token already used"),
            { Expired: true } => new RefreshTokenValidationResult(false, Reason: "Refresh token expired"),
            _ => new RefreshTokenValidationResult(true, FormatId(state.UserId), FormatTenant()),
        };
    }

    /// <inheritdoc />
    public async Task<RefreshTokenConsumeResult> ConsumeAsync(
        string refreshToken, string expectedTenantId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.NotFound);
        }

        var hash = Hash(refreshToken);
        // Every token here is the scope's tenant's: asked for another tenant, a token found is a mismatch, left unchanged.
        if (!IsCurrentTenant(expectedTenantId))
        {
            return await ReadStateAsync(hash, cancellationToken).ConfigureAwait(false) is null
                ? new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.NotFound)
                : new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.TenantMismatch);
        }

        var connection = await context.Database.OpenTenantConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using (var consume = new NpgsqlCommand(ConsumeSql, connection) { Parameters = { HashParameter(hash) } })
        {
            var reader = await consume.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            await using (reader.ConfigureAwait(false))
            {
                if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    return new RefreshTokenConsumeResult(
                        RefreshTokenConsumeStatus.Consumed,
                        FormatId(reader.GetGuid(0)),
                        FormatTenant(),
                        FormatId(reader.GetGuid(1)),
                        await reader.IsDBNullAsync(2, cancellationToken).ConfigureAwait(false) ? null : reader.GetString(2));
                }
            }
        }

        var state = await ReadStateAsync(hash, cancellationToken).ConfigureAwait(false);
        switch (state)
        {
            case null:
                return new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.NotFound);
            case { FamilyRevoked: true }:
                return new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.Revoked);
            case { Consumed: true }:
                // Reuse, even when the token has also expired or was revoked on its own: the session may be stolen.
                await ExecuteAsync(RevokeFamilySql, new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = state.FamilyId }, cancellationToken)
                    .ConfigureAwait(false);
                var tenant = currentTenant.TenantId;
                LogReuse(logger, tenant);
                return new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.Reused);
            case { TokenRevoked: true }:
                return new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.Revoked);
            case { Expired: true }:
                return new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.Expired);
            default:
                // Valid now but not consumed a moment ago: a concurrent change rolled back. Fail closed; the client signs in.
                return new RefreshTokenConsumeResult(RefreshTokenConsumeStatus.Revoked);
        }
    }

    /// <inheritdoc />
    public Task RevokeAsync(string refreshToken, CancellationToken cancellationToken = default) =>
        string.IsNullOrEmpty(refreshToken)
            ? Task.CompletedTask
            : ExecuteAsync(RevokeTokenFamilySql, HashParameter(Hash(refreshToken)), cancellationToken);

    /// <inheritdoc />
    public Task RevokeAllForUserAsync(string userId, CancellationToken cancellationToken = default) =>
        // Users are GUIDs: any other value owns no token.
        Guid.TryParse(userId, out var user)
            ? ExecuteAsync(RevokeUserSql, new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = user }, cancellationToken)
            : Task.CompletedTask;

    private async Task<TokenState?> ReadStateAsync(byte[] hash, CancellationToken cancellationToken)
    {
        var connection = await context.Database.OpenTenantConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(StateSql, connection) { Parameters = { HashParameter(hash) } };
        var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            return new TokenState(
                reader.GetGuid(0), reader.GetGuid(1), reader.GetBoolean(2), reader.GetBoolean(3), reader.GetBoolean(4), reader.GetBoolean(5));
        }
    }

    private async Task ExecuteAsync(string sql, NpgsqlParameter parameter, CancellationToken cancellationToken)
    {
        var connection = await context.Database.OpenTenantConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(sql, connection) { Parameters = { parameter } };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsCurrentTenant(string? tenantId) =>
        Guid.TryParse(tenantId, out var tenant) && tenant == currentTenant.TenantId;

    private string FormatTenant() => currentTenant.TenantId.ToString("D", CultureInfo.InvariantCulture);

    private static string FormatId(Guid id) => id.ToString("D", CultureInfo.InvariantCulture);

    private static Guid ParseId(string value, string parameterName) =>
        Guid.TryParse(value, out var id) ? id : throw new ArgumentException("The value is not a GUID.", parameterName);

    // A token is random and long, so an unsalted SHA-256 cannot be reversed; it is the lookup key.
    private static byte[] Hash(string refreshToken) => SHA256.HashData(Encoding.UTF8.GetBytes(refreshToken));

    private static NpgsqlParameter HashParameter(byte[] hash) => new() { NpgsqlDbType = NpgsqlDbType.Bytea, Value = hash };

    // timestamptz takes UTC only; a value of unspecified kind is taken as UTC, as the contract names it.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "A spent refresh token was presented again in tenant {TenantId}; its session is revoked.")]
    private static partial void LogReuse(ILogger logger, Guid tenantId);

    private sealed record TokenState(Guid UserId, Guid FamilyId, bool FamilyRevoked, bool Consumed, bool TokenRevoked, bool Expired);
}
