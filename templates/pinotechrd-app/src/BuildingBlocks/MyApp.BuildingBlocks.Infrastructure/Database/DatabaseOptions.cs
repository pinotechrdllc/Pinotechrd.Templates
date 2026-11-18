using Microsoft.Extensions.Options;
using Npgsql;

namespace MyApp.BuildingBlocks.Infrastructure.Database;

/// <summary>The connection string read from <see cref="DatabaseRegistration.ConnectionStringKey"/>. It is a secret.</summary>
internal sealed class DatabaseOptions
{
    /// <summary>The Npgsql connection string, or <see langword="null"/> when it is not configured.</summary>
    public string? ConnectionString { get; set; }

    /// <inheritdoc />
    public override string ToString() => nameof(DatabaseOptions);
}

/// <summary>
/// Requires a connection string that Npgsql accepts and that sets none of the settings <see cref="ConnectionSafety"/>
/// refuses (<c>Multiplexing</c>, <c>No Reset On Close</c>, a search path). The messages name the setting and never quote
/// its value or the parser's message, which can quote part of it.
/// </summary>
internal sealed class DatabaseOptionsValidator : IValidateOptions<DatabaseOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return ValidateOptionsResult.Fail(
                $"{DatabaseRegistration.ConnectionStringKey} is not set. Supply the PostgreSQL connection string through the "
                + "environment (ConnectionStrings__Database) or user secrets; it is never read from appsettings.json.");
        }

        if (Parse(options.ConnectionString) is not { } settings)
        {
            return ValidateOptionsResult.Fail(
                $"{DatabaseRegistration.ConnectionStringKey} is not a valid PostgreSQL connection string: it must be a list of "
                + "Npgsql 'Keyword=value' pairs with a Host, such as 'Host=127.0.0.1;Database=app;Username=app;Password=...'. "
                + "The value is not shown because it is a secret.");
        }

        return ConnectionSafety.Refusal(settings) is { } refusal
            ? ValidateOptionsResult.Fail($"{DatabaseRegistration.ConnectionStringKey} {refusal}")
            : ValidateOptionsResult.Success;
    }

    // Building a data source runs every check Npgsql makes on a connection string (format, keywords, values, Host)
    // without opening a connection. Null when it is unusable.
    private static NpgsqlConnectionStringBuilder? Parse(string connectionString)
    {
        try
        {
            using var dataSource = new NpgsqlDataSourceBuilder(connectionString).Build();
            return new NpgsqlConnectionStringBuilder(connectionString);
        }
#pragma warning disable CA1031 // Any exception means the value is unusable; its message may quote the value.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}
