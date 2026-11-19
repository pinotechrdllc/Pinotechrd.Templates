using Microsoft.Extensions.Options;
using MyApp.BuildingBlocks.Infrastructure.Database;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// Requires, in the migrator only, a pooled connection string whose <c>Maximum Pool Size</c> is at least
/// <see cref="DatabaseMigrator.MinimumPoolSize"/>, so a pool too small is an invalid setting found when the host starts,
/// not a failed migration. A missing or unusable connection string is left to <see cref="DatabaseOptionsValidator"/>.
/// The message names the setting and never quotes the connection string.
/// </summary>
internal sealed class MigratorPoolSizeValidator : IValidateOptions<DatabaseOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        NpgsqlConnectionStringBuilder settings;
        try
        {
            settings = new NpgsqlConnectionStringBuilder(options.ConnectionString ?? string.Empty);
        }
#pragma warning disable CA1031 // Any exception means the value is unusable, which the database validator reports.
        catch (Exception)
#pragma warning restore CA1031
        {
            return ValidateOptionsResult.Skip;
        }

        return settings.Pooling && settings.MaxPoolSize < DatabaseMigrator.MinimumPoolSize
            ? ValidateOptionsResult.Fail(
                $"{DatabaseRegistration.ConnectionStringKey} sets 'Maximum Pool Size' below {DatabaseMigrator.MinimumPoolSize}: the "
                + "migrator holds the global lock connection while a tenant's run holds its lock connection and its migration "
                + "connection. Raise it, or leave it out to use the default.")
            : ValidateOptionsResult.Success;
    }
}
