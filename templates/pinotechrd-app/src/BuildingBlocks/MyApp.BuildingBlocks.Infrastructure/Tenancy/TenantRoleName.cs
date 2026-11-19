using System.Globalization;
using System.Text.RegularExpressions;

namespace MyApp.BuildingBlocks.Infrastructure.Tenancy;

/// <summary>
/// The rule that names the database roles of tenants, and the check every role name passes before it reaches SQL.
/// <para>
/// Roles belong to the whole PostgreSQL cluster, while schemas, tables and the migrator's locks belong to one database.
/// So every role name is scoped to its database by the database's oid (<c>pg_database.oid</c>), which the migrator and
/// the connection guard both read from the server: the role of a tenant is <c>t</c>, the oid in decimal, <c>_</c>, then
/// the tenant's id as 32 lower-case hexadecimal digits: <c>t16384_0f8fad5bd9cb469fa16570867728950e</c>, at most 44
/// characters. Like the schema name it comes from the id only (never from a slug), so two tenants never share a role,
/// and two databases on one cluster never share one either. It differs from the schema name on purpose: a schema named
/// like the current role would be what <c>"$user"</c> in the default <c>search_path</c> resolves to. A tenant role is
/// <c>NOLOGIN</c>: the application's login assumes it on each tenant connection (ADR 0012). A database restored or
/// copied gets a new oid, so its roles are created again by the next migration.
/// </para>
/// </summary>
public static partial class TenantRoleName
{
    /// <summary>The first character of every tenant role.</summary>
    public const string Prefix = "t";

    /// <summary>The ending of the shared group role of a database (see <see cref="SharedFor"/>).</summary>
    public const string SharedSuffix = "_shared";

    /// <summary>The pattern a tenant or shared role name must match before it is used in SQL.</summary>
    public const string Pattern = "^t[1-9][0-9]{0,9}_([0-9a-f]{32}|shared)$";

    /// <summary>The oid of the current database, as the migrator and the guard read it.</summary>
    internal const string DatabaseOidSql =
        "SELECT oid FROM pg_catalog.pg_database WHERE datname = pg_catalog.current_database()";

    /// <summary>Gets the role of the tenant <paramref name="tenantId"/> in the database <paramref name="databaseOid"/>.</summary>
    /// <param name="databaseOid">The oid of the database (<c>pg_database.oid</c>).</param>
    /// <param name="tenantId">The tenant's id.</param>
    /// <returns>The role name.</returns>
    /// <exception cref="ArgumentException"><paramref name="tenantId"/> is <see cref="Guid.Empty"/>, or the oid is 0.</exception>
    public static string For(uint databaseOid, Guid tenantId)
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant id cannot be the empty GUID.", nameof(tenantId));
        }

        return DatabasePrefix(databaseOid) + "_" + tenantId.ToString("N");
    }

    /// <summary>
    /// Gets the group role every tenant role of the database <paramref name="databaseOid"/> is a member of:
    /// <c>USAGE</c> on <see cref="TenantSchemaName.ExtensionsSchema"/>, and <c>USAGE</c> plus <c>SELECT</c> on the shared
    /// tables the tenant contexts read (tables they exclude from their migrations, such as the catalog's). Nothing more.
    /// </summary>
    /// <param name="databaseOid">The oid of the database (<c>pg_database.oid</c>).</param>
    /// <returns>The role name, such as <c>t16384_shared</c>.</returns>
    /// <exception cref="ArgumentException">The oid is 0.</exception>
    public static string SharedFor(uint databaseOid) => DatabasePrefix(databaseOid) + SharedSuffix;

    /// <summary>Whether <paramref name="roleName"/> matches <see cref="Pattern"/>.</summary>
    /// <param name="roleName">The name to check.</param>
    /// <returns><see langword="true"/> when it may be used in SQL.</returns>
    public static bool IsValid(string? roleName) => roleName is not null && RoleNamePattern().IsMatch(roleName);

    /// <summary>Returns <paramref name="roleName"/> when <see cref="IsValid"/>, and throws otherwise.</summary>
    /// <param name="roleName">The name to check.</param>
    /// <returns>The same name.</returns>
    /// <exception cref="ArgumentException">The name is not valid. The message does not quote it.</exception>
    public static string Validate(string? roleName) =>
        IsValid(roleName)
            ? roleName!
            : throw new ArgumentException(
                $"The role name is not valid: it must match {Pattern}. It is refused before any SQL is sent.", nameof(roleName));

    /// <summary>The role name as a quoted SQL identifier, after <see cref="Validate"/>.</summary>
    internal static string Quoted(string roleName) => "\"" + Validate(roleName) + "\"";

    private static string DatabasePrefix(uint databaseOid) =>
        databaseOid == 0
            ? throw new ArgumentException("A database oid cannot be 0.", nameof(databaseOid))
            : Prefix + databaseOid.ToString(CultureInfo.InvariantCulture);

    // \z, not $: $ also matches before a final line feed. At most 1 + 10 + 1 + 32 = 44 characters, under PostgreSQL's 63.
    [GeneratedRegex(@"^t[1-9][0-9]{0,9}_(?:[0-9a-f]{32}|shared)\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoleNamePattern();
}
