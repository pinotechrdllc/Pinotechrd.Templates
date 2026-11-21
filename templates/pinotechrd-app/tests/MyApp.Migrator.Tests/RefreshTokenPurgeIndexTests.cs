using MyApp.Api.Tests.Database;
using MyApp.BuildingBlocks.Infrastructure.Tenancy;
using MyApp.Migrator.Tests.Support;

namespace MyApp.Migrator.Tests;

// The indexes the refresh token purger relies on, as the migrator leaves them in a tenant schema.
[Collection(PostgresContainer.Name)]
public class RefreshTokenPurgeIndexTests(PostgresFixture postgres)
{
    // Breaks when the migration AddRefreshTokenPurgeIndexes is missing or changed: the purge would scan the whole families
    // table, and the revoked_at index must stay partial (only revoked families).
    [Fact]
    public async Task AMigratedTenantHasTheAbsoluteExpiryIndexAndThePartialRevokedIndex()
    {
        var database = await postgres.CreateDatabaseAsync();
        Assert.Equal(0, (await MigratorRun.ExecuteAsync(database)).ExitCode);
        var tenant = Guid.NewGuid();
        await CatalogSeed.AddTenantAsync(database, tenant);
        var run = await MigratorRun.ExecuteAsync(database);
        Assert.True(run.ExitCode == 0, run.Report);

        var indexes = await Db.ColumnAsync(database, $"""
            SELECT indexname || '=' || indexdef FROM pg_indexes
            WHERE schemaname = '{TenantSchemaName.For(tenant)}' AND tablename = 'refresh_token_families'
              AND indexname IN ('ix_refresh_token_families_absolute_expires_at', 'ix_refresh_token_families_revoked_at')
            ORDER BY indexname
            """);

        Assert.Equal(2, indexes.Count);
        Assert.StartsWith("ix_refresh_token_families_absolute_expires_at=", indexes[0], StringComparison.Ordinal);
        Assert.Contains("(absolute_expires_at)", indexes[0], StringComparison.Ordinal);
        Assert.DoesNotContain(" WHERE ", indexes[0], StringComparison.Ordinal);
        Assert.StartsWith("ix_refresh_token_families_revoked_at=", indexes[1], StringComparison.Ordinal);
        Assert.Contains("(revoked_at) WHERE (revoked_at IS NOT NULL)", indexes[1], StringComparison.Ordinal);
    }
}
