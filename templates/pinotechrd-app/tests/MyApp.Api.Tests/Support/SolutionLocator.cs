namespace MyApp.Api.Tests.Support;

/// <summary>Finds the folders of the solution from the test output, so lookups work in a generated copy too.</summary>
internal static class SolutionLocator
{
    /// <summary>The folder that holds the solution file, found by walking up from the test output.</summary>
    public static string SolutionRoot()
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

    /// <summary>The absolute path of a file under the <c>contract/</c> folder of the solution.</summary>
    public static string ContractFile(string name) => Path.Combine(SolutionRoot(), "contract", name);

    /// <summary>The template definition next to the solution. Only the template source has it: the engine never copies it.</summary>
    public static string TemplateConfigFile => Path.Combine(SolutionRoot(), ".template.config", "template.json");

    /// <summary>Whether the tests run in the template source (which holds both architectures) rather than in a generated project.</summary>
    public static bool IsTemplateSource => File.Exists(TemplateConfigFile);
}
