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

    // Breaks when the API (directly or through any project it references) depends on the migrator.
    [Fact]
    public void ApiDoesNotReferenceTheMigrator()
    {
        var closure = Closure(ProjectFile(Root + ".Api"));

        Assert.NotEmpty(closure);
        Assert.DoesNotContain(Root + ".Migrator", closure);
    }
}
