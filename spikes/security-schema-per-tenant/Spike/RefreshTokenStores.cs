using System.Security.Cryptography;
using System.Text;
using Npgsql;
using Pinotechrd.Security.Interfaces;

namespace Pinotechrd.Spikes.SecuritySchemaPerTenant;

/// <summary>
/// Refresh tokens live in the catalog (catalog.refresh_tokens), not in a tenant schema: the library registers the store
/// as a singleton and a refresh request has no trusted tenant yet. Only a SHA-256 hash of the token is stored.
/// </summary>
public abstract class DbRefreshTokenStore(NpgsqlDataSource dataSource) : IRefreshTokenStore
{
    public const string CreateTableSql = """
        CREATE TABLE IF NOT EXISTS catalog.refresh_tokens (
            token_hash bytea PRIMARY KEY,
            user_id text NOT NULL,
            tenant_id text NOT NULL,
            expires_at timestamptz NOT NULL,
            revoked_at timestamptz NULL)
        """;

    protected NpgsqlDataSource DataSource { get; } = dataSource;

    protected static byte[] Hash(string token) => SHA256.HashData(Encoding.UTF8.GetBytes(token));

    public async Task SaveAsync(string userId, string tenantId, string refreshToken, DateTime expiresAtUtc)
    {
        await using var cmd = DataSource.CreateCommand(
            "INSERT INTO catalog.refresh_tokens (token_hash, user_id, tenant_id, expires_at) VALUES ($1, $2, $3, $4)");
        cmd.Parameters.AddWithValue(Hash(refreshToken));
        cmd.Parameters.AddWithValue(userId);
        cmd.Parameters.AddWithValue(tenantId);
        cmd.Parameters.AddWithValue(DateTime.SpecifyKind(expiresAtUtc, DateTimeKind.Utc));
        await cmd.ExecuteNonQueryAsync();
    }

    public abstract Task<RefreshTokenValidationResult> ValidateAsync(string refreshToken);

    public async Task RevokeAsync(string refreshToken)
    {
        await using var cmd = DataSource.CreateCommand(
            "UPDATE catalog.refresh_tokens SET revoked_at = now() WHERE token_hash = $1 AND revoked_at IS NULL");
        cmd.Parameters.AddWithValue(Hash(refreshToken));
        await cmd.ExecuteNonQueryAsync();
    }

    protected static async Task<RefreshTokenValidationResult> ReadAsync(NpgsqlCommand cmd)
    {
        await using var reader = await cmd.ExecuteReaderAsync();
        return await reader.ReadAsync()
            ? new RefreshTokenValidationResult(true, reader.GetString(0), reader.GetString(1))
            : new RefreshTokenValidationResult(false, Reason: "Invalid refresh token");
    }
}

/// <summary>
/// Validate consumes: one UPDATE ... RETURNING marks the token used and returns its owner. Of two concurrent calls
/// PostgreSQL lets one update the row; the other re-checks "revoked_at IS NULL" after the first commits and gets no row.
/// The library's later RevokeAsync then finds nothing to do.
/// </summary>
public sealed class AtomicRefreshTokenStore(NpgsqlDataSource dataSource) : DbRefreshTokenStore(dataSource)
{
    public override async Task<RefreshTokenValidationResult> ValidateAsync(string refreshToken)
    {
        await using var cmd = DataSource.CreateCommand("""
            UPDATE catalog.refresh_tokens SET revoked_at = now()
            WHERE token_hash = $1 AND revoked_at IS NULL AND expires_at > now()
            RETURNING user_id, tenant_id
            """);
        cmd.Parameters.AddWithValue(Hash(refreshToken));
        return await ReadAsync(cmd);
    }
}

/// <summary>Validate only reads, as the contract's names suggest and the in-memory default does. Racy by design.</summary>
public sealed class NaiveRefreshTokenStore(NpgsqlDataSource dataSource) : DbRefreshTokenStore(dataSource)
{
    public override async Task<RefreshTokenValidationResult> ValidateAsync(string refreshToken)
    {
        await using var cmd = DataSource.CreateCommand("""
            SELECT user_id, tenant_id FROM catalog.refresh_tokens
            WHERE token_hash = $1 AND revoked_at IS NULL AND expires_at > now()
            """);
        cmd.Parameters.AddWithValue(Hash(refreshToken));
        return await ReadAsync(cmd);
    }
}
