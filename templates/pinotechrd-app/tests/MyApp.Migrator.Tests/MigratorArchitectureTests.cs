using System.Xml.Linq;

namespace MyApp.Migrator.Tests;

// The migrator is a command of its own: it never carries the web layer or an endpoints project, and the API never carries
// the migrator. The rules read the project files (the whole reference closure) and the compiled references.
public class MigratorArchitectureTests
{
    private static readonly string Root = RootOf(typeof(MigratorArchitectureTests).Assembly.GetName().Name!);

    private static string RootOf(string testAssemblyName)
    {
        const string Suffix = ".Migrator.Tests";
        Assert.EndsWith(Suffix, testAssemblyName, StringComparison.Ordinal);
        return testAssemblyName[..^Suffix.Length];
    }

    private static string SolutionRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (directory.EnumerateFiles("*.slnx").Any() || directory.EnumerateFiles("*.sln").Any())
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException($"No solution file found above '{AppContext.BaseDirectory}'.");
    }

    private static string ProjectFile(string name) => Path.Combine(SolutionRoot(), "src", name, name + ".csproj");

    // Every project the project reaches through project references, directly or not, by project name.
    private static HashSet<string> Closure(string projectFile)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>([projectFile]);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            foreach (var reference in XDocument.Load(current).Descendants("ProjectReference").Select(e => (string)e.Attribute("Include")!))
            {
                var path = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(current)!, reference.Replace('\\', Path.DirectorySeparatorChar)));
                if (seen.Add(Path.GetFileNameWithoutExtension(path)))
                {
                    pending.Push(path);
                }
            }
        }

        return seen;
    }

    // Breaks when the migrator project (directly or through any project it references) depends on the web building block or
    // on a module's endpoints project.
    [Fact]
    public void MigratorReferencesNeitherTheWebBuildingBlockNorAnEndpointsProject()
    {
        var closure = Closure(ProjectFile(Root + ".Migrator"));

        Assert.NotEmpty(closure);
        Assert.DoesNotContain(Root + ".BuildingBlocks.Web", closure);
        Assert.DoesNotContain(closure, name => name.EndsWith(".Endpoints", StringComparison.Ordinal));
        Assert.DoesNotContain(Root + ".Api", closure);
    }

    // Breaks when the compiled migrator references the web building block, an endpoints assembly or the API.
    [Fact]
    public void MigratorAssemblyReferencesNeitherTheWebBuildingBlockNorAnEndpointsAssembly()
    {
        var references = typeof(MigratorHost).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(Root + ".BuildingBlocks.Web", references);
        Assert.DoesNotContain(Root + ".Api", references);
        Assert.DoesNotContain(references, name => name.StartsWith(Root, StringComparison.Ordinal) && name.EndsWith(".Endpoints", StringComparison.Ordinal));
    }

    private static string AccessApplicationProject() =>
        Directory.EnumerateFiles(Path.Combine(SolutionRoot(), "src"), Root + ".Modules.Access.Application.csproj", SearchOption.AllDirectories)
            .Single();

    private static bool IsSecurityOrEfCore(string name) =>
        name.StartsWith("Pinotechrd.Security", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.OrdinalIgnoreCase)
        || name.Contains("EntityFrameworkCore", StringComparison.OrdinalIgnoreCase);

    // Breaks when the Access application layer (or a project it reaches) declares a package of the security library or EF Core,
    // or reaches a project that does (infrastructure).
    [Fact]
    public void AccessApplicationProjectsDeclareNeitherTheSecurityLibraryNorEfCore()
    {
        var project = AccessApplicationProject();
        var projects = new List<string> { project };
        projects.AddRange(Closure(project).Select(name => Directory.EnumerateFiles(Path.Combine(SolutionRoot(), "src"), name + ".csproj", SearchOption.AllDirectories).Single()));

        Assert.True(projects.Count > 1);
        var packages = projects
            .SelectMany(p => XDocument.Load(p).Descendants("PackageReference").Select(e => (string)e.Attribute("Include")!))
            .ToList();
        Assert.DoesNotContain(packages, IsSecurityOrEfCore);
        Assert.DoesNotContain(Closure(project), name => name.EndsWith(".Infrastructure", StringComparison.Ordinal));
    }

    // Breaks when the compiled Access application layer references the security library or EF Core.
    [Fact]
    public void AccessApplicationAssemblyReferencesNeitherTheSecurityLibraryNorEfCore()
    {
        var assembly = System.Reflection.Assembly.Load(Root + ".Modules.Access.Application");

        var references = assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.NotEmpty(references);
        Assert.DoesNotContain(references, IsSecurityOrEfCore);
        Assert.DoesNotContain(references, name => name.StartsWith("Npgsql", StringComparison.OrdinalIgnoreCase));
    }

    // Breaks when the API (directly or through any project it references) depends on the migrator.
    [Fact]
    public void ApiDoesNotReferenceTheMigrator()
    {
        var closure = Closure(ProjectFile(Root + ".Api"));

        Assert.NotEmpty(closure);
        Assert.DoesNotContain(Root + ".Migrator", closure);
    }
}
