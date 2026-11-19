using MyApp.BuildingBlocks.Application.Security;

namespace MyApp.Modules.Access.Infrastructure.Seeding;

/// <summary>A subscription plan the migrator keeps in the catalog: its code, its name and the modules it includes.</summary>
/// <param name="Code">The plan's code, upper case, such as <c>STANDARD</c>.</param>
/// <param name="Name">The plan's display name.</param>
/// <param name="Modules">The modules it includes, as the permissions name them (<c>Access</c>), in ordinal order.</param>
public sealed record PlanDefinition(string Code, string Name, IReadOnlyList<string> Modules);

/// <summary>
/// Which modules each seeded plan includes, from the <see cref="PermissionCatalogue"/>: the modules are the first
/// segments of its permissions. <see cref="OperatorPlanCode"/> includes every module; <see cref="StandardPlanCode"/>, the
/// plan of the operator's customers, every module except <see cref="OperatorOnlyModules"/>.
/// </summary>
public static class PlanComposition
{
    /// <summary>The plan of the customers' tenants.</summary>
    public const string StandardPlanCode = "STANDARD";

    /// <summary>The plan of the operator's own tenant.</summary>
    public const string OperatorPlanCode = "OPERATOR";

    /// <summary>Gets the modules only the operator's plan includes: the administration of the platform itself.</summary>
    public static IReadOnlyList<string> OperatorOnlyModules { get; } = ["Platform"];

    /// <summary>Gets the plans for <paramref name="catalogue"/>: the standard plan, then the operator's.</summary>
    /// <param name="catalogue">The permissions the application declares.</param>
    /// <returns>The plans.</returns>
    public static IReadOnlyList<PlanDefinition> For(PermissionCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        var modules = ModulesOf(catalogue);
        return
        [
            new PlanDefinition(StandardPlanCode, "Standard", [.. modules.Where(m => !OperatorOnlyModules.Contains(m, StringComparer.OrdinalIgnoreCase))]),
            new PlanDefinition(OperatorPlanCode, "Operator", modules),
        ];
    }

    /// <summary>Gets the modules of <paramref name="catalogue"/>, each once, in ordinal order.</summary>
    /// <param name="catalogue">The permissions the application declares.</param>
    /// <returns>The module names, such as <c>Access</c>.</returns>
    public static IReadOnlyList<string> ModulesOf(PermissionCatalogue catalogue)
    {
        ArgumentNullException.ThrowIfNull(catalogue);

        // A valid permission name always has a first segment. Two spellings of one module share a code (the API refuses
        // them at start; the migrator keeps the first in ordinal order).
        return
        [
            .. catalogue.Permissions.Select(p => p[..p.IndexOf('.', StringComparison.Ordinal)])
                .Order(StringComparer.Ordinal)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }
}
