namespace MyApp.BuildingBlocks.Application.Security;

/// <summary>
/// Every permission the application declares: the permissions each module, and the host, registered with
/// <see cref="PermissionRegistration.AddPermissions"/>. It is a singleton, read when the host starts (an endpoint that
/// requires a permission missing from it stops the start) and by the code that seeds permissions. A name registered
/// twice is kept once. Two names that differ only by case are kept as given; the web building block refuses them when
/// the host starts.
/// </summary>
public sealed class PermissionCatalogue
{
    private readonly HashSet<string> _permissions;

    /// <summary>Creates a catalogue of <paramref name="permissions"/>.</summary>
    /// <param name="permissions">The permissions, each a valid <see cref="PermissionName"/>.</param>
    /// <exception cref="ArgumentException">A name is not a valid permission name.</exception>
    public PermissionCatalogue(IEnumerable<string> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);

        _permissions = new HashSet<string>(StringComparer.Ordinal);
        foreach (var permission in permissions)
        {
            PermissionName.Validate(permission, nameof(permissions));
            _permissions.Add(permission);
        }

        Permissions = [.. _permissions.Order(StringComparer.Ordinal)];
    }

    /// <summary>Gets the permissions, each once, in ordinal order.</summary>
    public IReadOnlyList<string> Permissions { get; }

    /// <summary>Returns whether <paramref name="permission"/> is registered, compared exactly (case included).</summary>
    /// <param name="permission">The permission.</param>
    /// <returns><see langword="true"/> when it is registered.</returns>
    public bool Contains(string permission) => _permissions.Contains(permission);
}
