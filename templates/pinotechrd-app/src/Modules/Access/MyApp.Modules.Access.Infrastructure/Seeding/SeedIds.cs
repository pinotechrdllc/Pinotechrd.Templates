using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace MyApp.Modules.Access.Infrastructure.Seeding;

/// <summary>
/// The ids of the rows the migrator seeds: name-based UUIDs, version 5 (RFC 9562, section 5.5), in a namespace of this
/// application, so a seeded row has the same id in every tenant and on every run, and running the seeding again finds it.
/// Modules use the security package's own <c>SecurityModuleId.FromCode</c> instead, which plans in the catalog rely on.
/// Codes are upper-cased before they are hashed, as the package stores them.
/// </summary>
public static class SeedIds
{
    /// <summary>The namespace of every seeded id. Fixed forever: changing it would change every id.</summary>
    public static readonly Guid Namespace = new("a3c4e2f1-6b7d-4c8e-9f10-2d3b4a5c6e7f");

    /// <summary>Gets the id of the system role <c>Administrator</c>, the same in every tenant.</summary>
    public static Guid AdministratorRole { get; } = Create("role:ADMINISTRATOR");

    /// <summary>Gets the id of the operator tenant's subscription in the catalog.</summary>
    public static Guid OperatorSubscription { get; } = Create("subscription:OPERATOR");

    /// <summary>Gets the id of the permission <paramref name="code"/> (<c>Access.Users.Read</c> or <c>ACCESS.USERS.READ</c>).</summary>
    /// <param name="code">The permission's name or code.</param>
    /// <returns>The id.</returns>
    public static Guid Permission(string code) => Create("permission:" + Normalize(code));

    /// <summary>Gets the id of the subscription plan <paramref name="code"/>, such as <c>STANDARD</c>.</summary>
    /// <param name="code">The plan's code.</param>
    /// <returns>The id.</returns>
    public static Guid Plan(string code) => Create("plan:" + Normalize(code));

    /// <summary>Gets the id of the row that gives the plan <paramref name="planId"/> the module <paramref name="moduleCode"/>.</summary>
    /// <param name="planId">The plan's id, as stored (a plan made by hand keeps its own).</param>
    /// <param name="moduleCode">The module's code, such as <c>Access</c>.</param>
    /// <returns>The id.</returns>
    public static Guid PlanModule(Guid planId, string moduleCode) =>
        Create(string.Create(CultureInfo.InvariantCulture, $"plan-module:{planId:D}:{Normalize(moduleCode)}"));

    /// <summary>Gets the id of the row that gives the role <paramref name="roleId"/> the permission <paramref name="permissionId"/>.</summary>
    /// <param name="roleId">The role's id.</param>
    /// <param name="permissionId">The permission's id, as stored.</param>
    /// <returns>The id.</returns>
    public static Guid RolePermission(Guid roleId, Guid permissionId) =>
        Create(string.Create(CultureInfo.InvariantCulture, $"role-permission:{roleId:D}:{permissionId:D}"));

    private static string Normalize(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        return code.Trim().ToUpperInvariant();
    }

    private static Guid Create(string name)
    {
        var bytes = Encoding.UTF8.GetBytes(name);
        var input = new byte[16 + bytes.Length];
        Namespace.TryWriteBytes(input, bigEndian: true, out _);
        bytes.CopyTo(input, 16);

        // SHA-1 is what RFC 9562 prescribes for version 5; it derives an id here, it protects nothing.
#pragma warning disable CA5350
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350

        var id = hash.AsSpan(0, 16).ToArray();
        id[6] = (byte)((id[6] & 0x0F) | 0x50);
        id[8] = (byte)((id[8] & 0x3F) | 0x80);
        return new Guid(id, bigEndian: true);
    }
}
