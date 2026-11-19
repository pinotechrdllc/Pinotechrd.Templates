using MyApp.BuildingBlocks.Infrastructure.Tenancy;

namespace MyApp.BuildingBlocks.Infrastructure.Migrations;

/// <summary>
/// Columns of a shared table that the application's login reads, and those it inserts, directly, on a connection that
/// assumes no tenant role (a shared context such as the catalog). After the shared contexts are migrated, every run of
/// the database migrator grants that login <c>USAGE</c> on the schema, <c>SELECT</c> on <see cref="Columns"/> and
/// <c>INSERT</c> on <see cref="InsertColumns"/> only, nothing else (no <c>UPDATE</c>, no <c>DELETE</c>). Registered with
/// <see cref="SharedPersistenceRegistration.AddApplicationRead"/> and
/// <see cref="SharedPersistenceRegistration.AddApplicationAppend"/>.
/// </summary>
public sealed class ApplicationRead
{
    internal ApplicationRead(string schema, string table, IReadOnlyList<string> columns, IReadOnlyList<string>? insertColumns = null)
    {
        ArgumentNullException.ThrowIfNull(columns);
        insertColumns ??= [];
        if (columns.Count == 0 && insertColumns.Count == 0)
        {
            throw new ArgumentException("An application read names at least one column.", nameof(columns));
        }

        Schema = TenantSchemaName.Validate(schema);
        Table = TenantSchemaName.Validate(table);
        Columns = Normalize(columns);
        InsertColumns = Normalize(insertColumns);
    }

    /// <summary>Gets the schema of the table.</summary>
    public string Schema { get; }

    /// <summary>Gets the table.</summary>
    public string Table { get; }

    /// <summary>Gets the columns the application reads, ordered; empty when it only inserts.</summary>
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Gets the columns the application inserts, ordered; empty when it only reads.</summary>
    public IReadOnlyList<string> InsertColumns { get; }

    /// <summary>The grant statements for <paramref name="quotedRole"/>; every name passed its pattern and is quoted.</summary>
    internal string GrantSql(string quotedRole)
    {
        var table = $"{TenantSchemaName.Quoted(Schema)}.{TenantSchemaName.Quoted(Table)}";
        var sql = $"GRANT USAGE ON SCHEMA {TenantSchemaName.Quoted(Schema)} TO {quotedRole}";
        if (Columns.Count > 0)
        {
            sql += $"; GRANT SELECT ({string.Join(", ", Columns.Select(TenantSchemaName.Quoted))}) ON {table} TO {quotedRole}";
        }

        if (InsertColumns.Count > 0)
        {
            sql += $"; GRANT INSERT ({string.Join(", ", InsertColumns.Select(TenantSchemaName.Quoted))}) ON {table} TO {quotedRole}";
        }

        return sql;
    }

    private static string[] Normalize(IReadOnlyList<string> columns) =>
        [.. columns.Select(TenantSchemaName.Validate).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
}
