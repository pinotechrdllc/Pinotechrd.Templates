using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// Columns of a shared table that the application's login reads directly, on a connection that assumes no tenant role
/// (a shared context such as the catalog). After the shared contexts are migrated, every run of the database migrator
/// grants that login <c>USAGE</c> on the schema and <c>SELECT</c> on these columns only, nothing else. Registered with
/// <see cref="SharedPersistenceRegistration.AddApplicationRead"/>.
/// </summary>
public sealed class ApplicationRead
{
    internal ApplicationRead(string schema, string table, IReadOnlyList<string> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);
        if (columns.Count == 0)
        {
            throw new ArgumentException("An application read names at least one column.", nameof(columns));
        }

        Schema = TenantSchemaName.Validate(schema);
        Table = TenantSchemaName.Validate(table);
        Columns = [.. columns.Select(TenantSchemaName.Validate).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>Gets the schema of the table.</summary>
    public string Schema { get; }

    /// <summary>Gets the table.</summary>
    public string Table { get; }

    /// <summary>Gets the columns the application reads, ordered.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>The grant statements for <paramref name="quotedRole"/>; every name passed its pattern and is quoted.</summary>
    internal string GrantSql(string quotedRole) =>
        $"GRANT USAGE ON SCHEMA {TenantSchemaName.Quoted(Schema)} TO {quotedRole}; "
        + $"GRANT SELECT ({string.Join(", ", Columns.Select(TenantSchemaName.Quoted))}) "
        + $"ON {TenantSchemaName.Quoted(Schema)}.{TenantSchemaName.Quoted(Table)} TO {quotedRole}";
}
