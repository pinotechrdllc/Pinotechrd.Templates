using System.Xml.Linq;

namespace MyApp.Api.Tests.Architecture.Modular;

/// <summary>
/// The dependency rules of a modular project, as functions over assembly names: an assembly name and the names it
/// references go in, one message per violation comes out. Nothing here loads an assembly, so the rules are exercised on
/// synthetic graphs as well as on the real ones, which matters because a generated project has a single module and the
/// rules between modules have nothing to look at in it.
/// </summary>
internal static class ModuleReferenceRules
{
    private const string Domain = "Domain";
    private const string Contracts = "Contracts";
    private const string Application = "Application";
    private const string Infrastructure = "Infrastructure";
    private const string Endpoints = "Endpoints";

    /// <summary>
    /// What a module project may reference among the solution's assemblies: Contracts nothing, Domain the domain building
    /// block, Application its own Domain and Contracts and the domain and application building blocks, Infrastructure its
    /// own Application, Domain and Contracts and the domain, application and infrastructure building blocks (never the web
    /// one), Endpoints every project of its module and every building block. Application, Infrastructure and Endpoints may
    /// also use the Contracts of another module, which <see cref="CrossModuleViolations"/> then confines to Contracts.
    /// No module project references the host.
    /// </summary>
    public static IReadOnlyList<string> LayerViolations(string assembly, IEnumerable<string> references, string root)
    {
        if (ModuleAssemblyNames.Parse(assembly, root) is not { } self)
        {
            return [];
        }

        string Own(string layer) => $"{root}.Modules.{self.Module}.{layer}";
        string BuildingBlock(string name) => $"{root}.BuildingBlocks.{name}";

        string[] allowed = self.Layer switch
        {
            Contracts => [],
            Domain => [BuildingBlock(Domain)],
            Application => [Own(Domain), Own(Contracts), BuildingBlock(Domain), BuildingBlock(Application)],
            Infrastructure =>
            [
                Own(Application), Own(Domain), Own(Contracts),
                BuildingBlock(Domain), BuildingBlock(Application), BuildingBlock(Infrastructure),
            ],
            _ => [Own(Contracts), Own(Domain), Own(Application), Own(Infrastructure)],
        };

        return
        [
            .. references
                .Where(r => ReferenceRules.HasPrefix(r, root))
                .Where(r => !allowed.Contains(r, StringComparer.Ordinal))
                .Where(r => !(self.Layer == Endpoints && ReferenceRules.HasPrefix(r, root + ".BuildingBlocks")))
                .Where(r => !(self.Layer is Application or Infrastructure or Endpoints && IsContractsOfAnotherModule(r, self, root)))
                .Select(r => $"Assembly '{assembly}' must not reference solution assembly '{r}'."),
        ];
    }

    // The Contracts of another module are the one thing a module may use of it (README, Boundaries); whether a project may
    // use them is a matter of its layer, and the rule above gives them to Application, Infrastructure and Endpoints only.
    private static bool IsContractsOfAnotherModule(string reference, ModuleAssemblyName self, string root) =>
        ModuleAssemblyNames.Parse(reference, root) is { Layer: Contracts } other && other.Module != self.Module;

    /// <summary>
    /// The framework rules of a module project. Domain and Contracts use the base class library only (the denylist of the
    /// domain building block names what is reported first); Application uses no web or data framework.
    /// </summary>
    public static IReadOnlyList<string> FrameworkViolations(string assembly, IEnumerable<string> references, string root)
    {
        var layer = ModuleAssemblyNames.Parse(assembly, root)?.Layer;
        var outside = references.Where(r => !ReferenceRules.HasPrefix(r, root)).ToList();
        var messages = new List<string>();
        switch (layer)
        {
            case Domain or Contracts:
                foreach (var reference in outside)
                {
                    if (ReferenceRules.FrameworkPrefixesForbiddenInDomain.FirstOrDefault(p => ReferenceRules.HasPrefix(reference, p)) is { } prefix)
                    {
                        messages.Add($"Assembly '{assembly}' must not reference '{reference}' (forbidden '{prefix}*').");
                    }
                    else if (!ReferenceRules.IsBaseClassLibrary(reference))
                    {
                        messages.Add($"Assembly '{assembly}' must not reference '{reference}' (only the base class library is allowed).");
                    }
                }

                break;
            case Application:
                foreach (var reference in outside)
                {
                    if (ReferenceRules.FrameworkPrefixesForbiddenInApplication.FirstOrDefault(p => ReferenceRules.HasPrefix(reference, p)) is { } prefix)
                    {
                        messages.Add($"Assembly '{assembly}' must not reference '{reference}' (forbidden '{prefix}*').");
                    }
                }

                break;
            default:
                break;
        }

        return messages;
    }

    /// <summary>A module references only the Contracts of another module: Domain, Application, Infrastructure and Endpoints are private to it.</summary>
    public static IReadOnlyList<string> CrossModuleViolations(string assembly, IEnumerable<string> references, string root)
    {
        if (ModuleAssemblyNames.Parse(assembly, root) is not { } self)
        {
            return [];
        }

        return
        [
            .. references
                .Select(r => (Name: r, Parsed: ModuleAssemblyNames.Parse(r, root)))
                .Where(r => r.Parsed is not null && r.Parsed.Module != self.Module && r.Parsed.Layer != Contracts)
                .Select(r => $"Module '{self.Module}' ('{assembly}') must reference only the Contracts of module '{r.Parsed!.Module}', not '{r.Name}'."),
        ];
    }

    /// <summary>
    /// Only the host references the Endpoints project of a module, and only the Endpoints project of a module references
    /// that module's Infrastructure. Checked for any assembly of the solution.
    /// </summary>
    public static IReadOnlyList<string> AccessViolations(string assembly, IEnumerable<string> references, string root)
    {
        var isHost = assembly == root + ".Api";
        var self = ModuleAssemblyNames.Parse(assembly, root);
        var messages = new List<string>();
        foreach (var reference in references)
        {
            if (ModuleAssemblyNames.Parse(reference, root) is not { } target)
            {
                continue;
            }

            if (target.Layer == Endpoints && !isHost)
            {
                messages.Add($"Assembly '{assembly}' must not reference '{reference}': only the host references the Endpoints project of a module.");
            }
            else if (target.Layer == Infrastructure && !(self is { Layer: Endpoints } && self.Module == target.Module))
            {
                messages.Add($"Assembly '{assembly}' must not reference '{reference}': only the Endpoints project of the module references its Infrastructure.");
            }
        }

        return messages;
    }

    /// <summary>No building block references a module.</summary>
    public static IReadOnlyList<string> BuildingBlockViolations(string assembly, IEnumerable<string> references, string root)
    {
        if (!ReferenceRules.HasPrefix(assembly, root + ".BuildingBlocks"))
        {
            return [];
        }

        return
        [
            .. references
                .Where(r => ModuleAssemblyNames.IsModuleAssembly(r, root))
                .Select(r => $"Building block '{assembly}' must not reference module assembly '{r}'."),
        ];
    }

    /// <summary>The host's own code uses the Endpoints project of a module and no other project of it.</summary>
    public static IReadOnlyList<string> HostCodeViolations(string assembly, IEnumerable<string> references, string root)
    {
        if (assembly != root + ".Api")
        {
            return [];
        }

        return
        [
            .. references
                .Where(r => ModuleAssemblyNames.IsModuleAssembly(r, root) && !ModuleAssemblyNames.IsEndpoints(r, root))
                .Select(r => $"The host '{assembly}' must reference only the Endpoints project of a module, not '{r}'."),
        ];
    }

    /// <summary>The project references the host lists in its project file: only the Endpoints project of a module may be a module project.</summary>
    public static IReadOnlyList<string> HostProjectFileViolations(IEnumerable<string> projectNames, string root) =>
    [
        .. projectNames
            .Where(p => ModuleAssemblyNames.IsModuleAssembly(p, root) && !ModuleAssemblyNames.IsEndpoints(p, root))
            .Select(p => $"The host's project file references '{p}': it may reference only the Endpoints project of a module."),
    ];

    /// <summary>
    /// The names of the projects a project file references. The include path is read as text: its separators are
    /// backslashes, which are not path separators on Linux, so it is split on both kinds.
    /// </summary>
    public static IReadOnlyList<string> ProjectReferencesOf(XDocument project) =>
        [.. project.Descendants("ProjectReference").Select(r => ProjectNameOf((string)r.Attribute("Include")!))];

    /// <summary>The project name in an include path, whatever its separators: a path ending in <c>Y.csproj</c> gives <c>Y</c>.</summary>
    public static string ProjectNameOf(string includePath)
    {
        var file = includePath.Split('\\', '/')[^1];
        return file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? file[..^".csproj".Length] : file;
    }
}
