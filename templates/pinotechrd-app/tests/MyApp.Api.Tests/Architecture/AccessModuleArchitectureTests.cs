using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using MyApp.Api.Tests.Support;
using MyApp.BuildingBlocks.Web;

namespace MyApp.Api.Tests.Architecture;

/// <summary>
/// The shape of the Access module, which ships with every generated project (clean and modular alike), so these rules do
/// not live in the modular-only folder. They read names and project files, never the module's code.
/// </summary>
public class AccessModuleArchitectureTests(ApiHostFactory host) : IClassFixture<ApiHostFactory>
{
    private const string Module = "Access";
    private const string SecurityLibrary = "Pinotechrd.Security";

    private static readonly string[] SecurityConsumers = ["Infrastructure", "Endpoints"];

    private static string Root => ReferenceRules.SolutionRoot;

    private static string ProjectName(string layer) => $"{Root}.Modules.{Module}.{layer}";

    // Breaks when the module lacks one of its five projects, or has a sixth.
    [Fact]
    public void AccessHasExactlyTheFiveProjectsOfAModule()
    {
        var names = ModuleAssemblies.DiscoveredNames().Where(n => ModuleAssemblyNames.Parse(n, Root)?.Module == Module).Order(StringComparer.Ordinal);

        Assert.Equal(ModuleAssemblyNames.Layers.Select(ProjectName).Order(StringComparer.Ordinal), names);
    }

    // Breaks when a project file of the module is not one of the five.
    [Fact]
    public void AccessProjectFilesAreExactlyTheFiveProjectsOfAModule()
    {
        var files = ProjectFiles(Path.Combine(SolutionLocator.SolutionRoot(), "src", "Modules", Module))
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .Order(StringComparer.Ordinal);

        Assert.Equal(ModuleAssemblyNames.Layers.Select(ProjectName).Order(StringComparer.Ordinal), files);
    }

    // Breaks when the security library is referenced from the domain, the contracts, the application, a building block,
    // the host or any other module (compiled references and project files are both read).
    [Fact]
    public void OnlyAccessInfrastructureAndAccessEndpointsReferenceThePinotechrdSecurityLibrary()
    {
        var allowed = new[] { ProjectName("Infrastructure"), ProjectName("Endpoints") };

        var compiled = ModuleAssemblies.SolutionAssemblies()
            .Where(a => !allowed.Contains(ReferenceRules.NameOf(a), StringComparer.Ordinal))
            .SelectMany(a => ReferenceRules.ReferencedNames(a)
                .Where(r => ReferenceRules.HasPrefix(r, SecurityLibrary))
                .Select(r => $"Assembly '{ReferenceRules.NameOf(a)}' must not reference '{r}'."));

        var declared = ProjectFiles(Path.Combine(SolutionLocator.SolutionRoot(), "src"))
            .Where(f => !allowed.Contains(Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal))
            .Where(f => PackageNames(f).Any(p => ReferenceRules.HasPrefix(p, SecurityLibrary)))
            .Select(f => $"Project '{Path.GetFileName(f)}' must not declare a package reference to the security library.");

        ReferenceRules.AssertNone([.. compiled, .. declared]);
    }

    // Guards the rule above against being vacuous: the library is declared where it is allowed.
    [Fact]
    public void AccessInfrastructureAndAccessEndpointsDoUseThePinotechrdSecurityLibrary()
    {
        var used = SecurityConsumers
            .SelectMany(PackageNamesOfProject)
            .Where(p => ReferenceRules.HasPrefix(p, SecurityLibrary))
            .ToList();

        Assert.NotEmpty(used);
    }

    // Breaks when the domain or the contracts of Access reference any package or any framework outside the base class library.
    [Theory]
    [InlineData("Domain")]
    [InlineData("Contracts")]
    public void AccessDomainAndContractsUseOnlyTheBaseClassLibrary(string layer)
    {
        var assembly = ModuleAssemblies.OfModule(Module).Single(m => m.Name.Layer == layer).Assembly;

        ReferenceRules.AssertNone(ModuleReferenceRules.FrameworkViolations(ProjectName(layer), ReferenceRules.ReferencedNames(assembly), Root));
        Assert.Empty(PackageNamesOfProject(layer));
    }

    // Breaks when a project of Access references what its layer must not, another module's inner projects or the host.
    [Fact]
    public void EachProjectOfAccessReferencesOnlyWhatItsLayerMayReference()
    {
        var violations = ModuleAssemblies.OfModule(Module).SelectMany(m =>
            ModuleReferenceRules.LayerViolations(ProjectName(m.Name.Layer), ReferenceRules.ReferencedNames(m.Assembly), Root)
                .Concat(ModuleReferenceRules.CrossModuleViolations(ProjectName(m.Name.Layer), ReferenceRules.ReferencedNames(m.Assembly), Root)))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Breaks when anything but the host references the endpoints of Access, or anything but its own endpoints its infrastructure.
    [Fact]
    public void OnlyTheHostReferencesAccessEndpointsAndOnlyItsEndpointsReferenceItsInfrastructure()
    {
        var violations = ModuleAssemblies.SolutionAssemblies()
            .SelectMany(a => ModuleReferenceRules.AccessViolations(ReferenceRules.NameOf(a), ReferenceRules.ReferencedNames(a), Root))
            .Where(v => v.Contains($".Modules.{Module}.", StringComparison.Ordinal))
            .ToList();

        ReferenceRules.AssertNone(violations);
    }

    // Breaks when the host stops composing Access (or composes it twice), in a clean or a modular project.
    [Fact]
    public void TheHostComposesAccessExactlyOnce()
    {
        var modules = host.Services.GetServices<IModule>().Where(m => m.EndpointsAssembly.GetName().Name == ProjectName("Endpoints")).ToList();

        var access = Assert.Single(modules);
        Assert.Equal(Module, access.Name);
    }

    // Breaks when another module of a modular project is not composed next to Access, or a module is composed whose
    // assemblies the project does not have. The stamp module is not named here: its name is replaced by --module, and a
    // clean output must not contain it.
    [Fact]
    public void TheHostComposesEveryOtherModuleExactlyWhenItsAssembliesAreThere()
    {
        var present = ModuleAssemblies.OfLayer("Endpoints")
            .Where(m => m.Name.Module != Module)
            .Select(m => m.Assembly.GetName().Name!)
            .Order(StringComparer.Ordinal);

        var composed = host.Services.GetServices<IModule>()
            .Select(m => m.EndpointsAssembly.GetName().Name!)
            .Where(name => name != ProjectName("Endpoints"))
            .Order(StringComparer.Ordinal);

        Assert.Equal(present, composed);
    }

    // Breaks when the project file of the host stops referencing Access, in either layout.
    [Fact]
    public void TheHostProjectFileReferencesTheAccessEndpointsProject()
    {
        var hostName = ReferenceRules.NameOf(ReferenceRules.Api);
        var file = Path.Combine(SolutionLocator.SolutionRoot(), "src", hostName, hostName + ".csproj");
        var references = ModuleReferenceRules.ProjectReferencesOf(XDocument.Load(file));

        Assert.Contains(ProjectName("Endpoints"), references);
        ReferenceRules.AssertNone(ModuleReferenceRules.HostProjectFileViolations(references, Root));
    }

    private static IEnumerable<string> ProjectFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*.csproj", SearchOption.AllDirectories)
            .Where(f => !f.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(s => s is "obj" or "bin"));

    private static IEnumerable<string> PackageNames(string projectFile) =>
        XDocument.Load(projectFile).Descendants("PackageReference").Select(r => (string?)r.Attribute("Include") ?? (string?)r.Attribute("Update") ?? string.Empty);

    private static List<string> PackageNamesOfProject(string layer) =>
        [.. ProjectFiles(Path.Combine(SolutionLocator.SolutionRoot(), "src", "Modules", Module))
            .Where(f => Path.GetFileNameWithoutExtension(f) == ProjectName(layer))
            .SelectMany(PackageNames)];
}
