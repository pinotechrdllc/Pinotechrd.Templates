using System.Text.RegularExpressions;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// The rule that names a tenant's schema, and the check every schema name passes before it reaches SQL.
/// <para>
/// The schema of a tenant is <c>tenant_</c> followed by the tenant's id as 32 lower-case hexadecimal digits
/// (<see cref="Guid.ToString(string)"/> with <c>N</c>): <c>tenant_0f8fad5bd9cb469fa16570867728950e</c>, 39 characters.
/// It is injective: the <c>N</c> form is one-to-one with the GUID and the prefix is fixed, so two tenants never share a
/// schema. It never comes from a name a user chose (a slug can be renamed, reused or crafted), and it cannot be
/// <c>public</c>, a <c>pg_</c> schema or any shared schema of the application.
/// </para>
/// </summary>
public static partial class TenantSchemaName
{
    /// <summary>The prefix of every tenant schema.</summary>
    public const string Prefix = "tenant_";

    /// <summary>
    /// The one shared schema that holds PostgreSQL extensions (<c>citext</c>, <c>pg_trgm</c>...), second on every tenant
    /// connection's <c>search_path</c>, after the tenant's schema. The migrator creates it and installs there the extensions
    /// the tenant models declare. Never a tenant schema (it has no <see cref="Prefix"/>).
    /// </summary>
    public const string ExtensionsSchema = "extensions";

    /// <summary>The pattern a schema name must match before it is used in SQL: a PostgreSQL identifier that needs no quoting.</summary>
    public const string Pattern = "^[a-z][a-z0-9_]{0,62}$";

    /// <summary>Gets the schema of the tenant <paramref name="tenantId"/>.</summary>
    /// <param name="tenantId">The tenant's id.</param>
    /// <returns>The schema name.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is <see cref="Guid.Empty"/>.</exception>
    public static string For(Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant id cannot be the empty GUID.", nameof(tenantId));
        }

        return Prefix + tenantId.ToString("N");
    }

    /// <summary>Whether <paramref name="schemaName"/> matches <see cref="Pattern"/>.</summary>
    /// <param name="schemaName">The name to check.</param>
    /// <returns><see langword="true"/> when it may be used in SQL.</returns>
    public static bool IsValid(string? schemaName) => schemaName is not null && SchemaNamePattern().IsMatch(schemaName);

    /// <summary>Returns <paramref name="schemaName"/> when it matches <see cref="Pattern"/>, and throws otherwise.</summary>
    /// <param name="schemaName">The name to check.</param>
    /// <returns>The same name.</returns>
    /// <exception cref="ArgumentException">The name does not match <see cref="Pattern"/>. The message does not quote it.</exception>
    public static string Validate(string? schemaName) =>
        IsValid(schemaName)
            ? schemaName!
            : throw new ArgumentException(
                $"The schema name is not valid: it must match {Pattern}. It is refused before any SQL is sent.", nameof(schemaName));

    /// <summary>
    /// The <c>search_path</c> of a tenant connection: the tenant's schema, then <see cref="ExtensionsSchema"/>, both quoted,
    /// after <see cref="Validate"/>. Never <c>public</c>.
    /// </summary>
    internal static string SearchPathFor(string schemaName) => Quoted(schemaName) + ", " + Quoted(ExtensionsSchema);

    /// <summary>The schema name as a quoted SQL identifier, after <see cref="Validate"/>.</summary>
    internal static string Quoted(string schemaName) => "\"" + Validate(schemaName) + "\"";

    // \z, not $: $ also matches before a final line feed.
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex SchemaNamePattern();
}
