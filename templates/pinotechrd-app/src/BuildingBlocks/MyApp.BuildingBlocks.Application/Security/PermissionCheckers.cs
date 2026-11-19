using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Application.Identity;

namespace MyApp.BuildingBlocks.Application.Security;

/// <summary>
/// Which <see cref="IPermissionChecker"/> owns the permissions of each module: one checker per module, registered with
/// <see cref="PermissionRegistration.AddPermissionChecker{TChecker}"/> (a keyed scoped service whose key is the module
/// name). A singleton, read from the registrations when it is first resolved; the web building block resolves it when
/// the host starts and refuses to start on any of its <see cref="Problems"/>.
/// </summary>
public sealed class PermissionCheckerMap
{
    private readonly HashSet<string> _modules;

    internal PermissionCheckerMap(IServiceCollection services, ServiceDescriptor router)
    {
        var problems = new List<string>();
        var keyed = services.Where(d => d.ServiceType == typeof(IPermissionChecker) && d.IsKeyedService).ToList();
        foreach (var descriptor in keyed.Where(d => d.ServiceKey is not string module || !PermissionName.IsValidModule(module)))
        {
            problems.Add($"A permission checker ({(descriptor.KeyedImplementationType ?? descriptor.KeyedImplementationInstance?.GetType())?.FullName ?? "a factory"}) is registered for '{descriptor.ServiceKey}', which is not a module name: register it with AddPermissionChecker<TChecker>(\"Module\").");
        }

        var claims = keyed.Where(d => d.ServiceKey is string module && PermissionName.IsValidModule(module))
            .GroupBy(d => (string)d.ServiceKey!, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var clash in claims.Where(g => g.Count() > 1))
        {
            problems.Add($"Two or more permission checkers claim the module '{clash.Key}' ({string.Join(", ", clash.Select(d => (d.KeyedImplementationType ?? d.KeyedImplementationInstance?.GetType())?.FullName ?? "a factory"))}); each module has one.");
        }

        foreach (var direct in services.Where(d => d.ServiceType == typeof(IPermissionChecker) && !d.IsKeyedService && !ReferenceEquals(d, router)))
        {
            problems.Add($"{(direct.ImplementationType ?? direct.ImplementationInstance?.GetType())?.FullName ?? "A permission checker"} is registered as IPermissionChecker itself, which would replace the routing to the modules' checkers: register it with AddPermissionChecker<TChecker>(\"Module\") instead.");
        }

        _modules = new HashSet<string>(claims.Select(g => g.Key), StringComparer.Ordinal);
        Modules = [.. _modules.Order(StringComparer.Ordinal)];
        Problems = problems;
    }

    /// <summary>Gets the modules that have a checker, in ordinal order.</summary>
    public IReadOnlyList<string> Modules { get; }

    /// <summary>Gets what is wrong with the registrations; empty when the host may start.</summary>
    public IReadOnlyList<string> Problems { get; }

    /// <summary>Returns whether <paramref name="module"/> has a checker, compared exactly.</summary>
    /// <param name="module">The module name, the first segment of a permission.</param>
    /// <returns><see langword="true"/> when a checker owns the module.</returns>
    public bool Owns(string module) => _modules.Contains(module);
}

/// <summary>
/// The <see cref="IPermissionChecker"/> the application asks: it sends each permission to the checker of its module, the
/// first segment of <c>{Module}.{Resource}.{Action}</c> (<c>Access.Users.Read</c> goes to the checker of
/// <c>Access</c>), and denies a permission whose module has no checker. With no checker registered it denies
/// everything.
/// </summary>
/// <param name="map">The owners of the modules.</param>
/// <param name="services">The scope's services, to resolve the module's checker.</param>
public sealed class PermissionCheckerRouter(PermissionCheckerMap map, IServiceProvider services) : IPermissionChecker
{
    /// <inheritdoc />
    public ValueTask<bool> HasPermissionAsync(ICurrentUser user, string permission, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(user);

        var dot = permission?.IndexOf('.', StringComparison.Ordinal) ?? -1;
        if (permission is null || dot <= 0 || !PermissionName.IsValid(permission) || !map.Owns(permission[..dot]))
        {
            return ValueTask.FromResult(false);
        }

        return services.GetRequiredKeyedService<IPermissionChecker>(permission[..dot]).HasPermissionAsync(user, permission, cancellationToken);
    }
}
