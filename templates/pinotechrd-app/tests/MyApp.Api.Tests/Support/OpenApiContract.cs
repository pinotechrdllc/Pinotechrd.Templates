using System.Text.Json.Nodes;

namespace MyApp.Api.Tests.Support;

/// <summary>Helpers to read and compare OpenAPI documents as JSON.</summary>
internal static class OpenApiContract
{
    public const string ProblemJson = "application/problem+json";

    public const string ContractFileName = "openapi.v1.json";

    public static string ContractPath => SolutionLocator.ContractFile(ContractFileName);

    public static string ApiProjectName => ApiAssembly.Assembly.GetName().Name!;

    /// <summary>The command the README gives to regenerate the committed contract.</summary>
    public static string RegenerateCommand => $"dotnet build src/{ApiProjectName} -p:UpdateContract=true";

    public static JsonObject Parse(string json) => JsonNode.Parse(json)!.AsObject();

    public static JsonObject ReadCommitted() => Parse(File.ReadAllText(ContractPath));

    public static JsonObject Paths(JsonObject document) => document["paths"]!.AsObject();

    /// <summary>Every operation of the document as (path, method, operation).</summary>
    public static List<(string Path, string Method, JsonObject Operation)> Operations(JsonObject document) =>
        [.. Paths(document).SelectMany(p => p.Value!.AsObject()
            .Where(m => m.Value is JsonObject)
            .Select(m => (p.Key, m.Key, m.Value!.AsObject())))];

    /// <summary>The tags of an operation.</summary>
    public static string[] Tags(JsonObject operation) =>
        operation["tags"]?.AsArray().Select(t => t!.GetValue<string>()).ToArray() ?? [];

    /// <summary>Whether the operation, or its path item, declares OpenAPI parameters.</summary>
    public static bool HasParameters(JsonObject document, string path, JsonObject operation) =>
        operation["parameters"] is JsonArray { Count: > 0 } || Paths(document)[path]!["parameters"] is JsonArray { Count: > 0 };

    /// <summary>The names of the component schemas that problem+json responses of the operations point to.</summary>
    public static List<string> ProblemSchemaNames(JsonObject document) =>
        [.. Operations(document)
            .SelectMany(o => o.Operation["responses"]!.AsObject())
            .Select(r => r.Value!["content"]?[ProblemJson]?["schema"]?["$ref"]?.GetValue<string>())
            .OfType<string>()
            .Select(r => r[(r.LastIndexOf('/') + 1)..])
            .Distinct(StringComparer.Ordinal)];

    /// <summary>The title a fresh generation produces: the root name of the API assembly followed by " API".</summary>
    public static string ExpectedTitle
    {
        get
        {
            const string Suffix = ".Api";
            Assert.EndsWith(Suffix, ApiProjectName, StringComparison.Ordinal);
            return ApiProjectName[..^Suffix.Length] + " API";
        }
    }

    /// <summary>Resolves a <c>#/components/schemas/X</c> reference, or returns the node itself when it is inline.</summary>
    public static JsonObject ResolveSchema(JsonObject document, JsonObject schema)
    {
        if (schema["$ref"]?.GetValue<string>() is not { } reference)
        {
            return schema;
        }

        const string Prefix = "#/components/schemas/";
        Assert.StartsWith(Prefix, reference, StringComparison.Ordinal);
        return document["components"]!["schemas"]![reference[Prefix.Length..]]!.AsObject();
    }

    /// <summary>The schema of a response in the given media type, or null when the response does not declare it.</summary>
    public static JsonObject? ResponseSchema(JsonObject operation, string status, string mediaType) =>
        operation["responses"]?[status]?["content"]?[mediaType]?["schema"]?.AsObject();

    /// <summary>
    /// Compares the served document with the committed one, ignoring <c>servers</c>. Returns null when they are
    /// equal, otherwise a message that lists what differs and how to regenerate the file.
    /// </summary>
    public static string? Compare(JsonObject committed, JsonObject served)
    {
        var expected = (JsonObject)committed.DeepClone();
        var actual = (JsonObject)served.DeepClone();
        expected.Remove("servers");
        actual.Remove("servers");
        if (JsonNode.DeepEquals(expected, actual))
        {
            return null;
        }

        var differences = new List<string>();
        Diff(string.Empty, expected, actual, differences);

        var affected = differences
            .Select(d => d.Split(' ')[^1])
            .Select(Affected)
            .Where(a => a is not null)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal);

        return $"The API document served by the application differs from contract/{ContractFileName}. "
            + $"Affected: [{string.Join(", ", affected)}]. "
            + $"Differences (contract vs served): {string.Join("; ", differences.Take(30))}"
            + (differences.Count > 30 ? $"; and {differences.Count - 30} more" : string.Empty)
            + $". Regenerate the file with `{RegenerateCommand}` and commit it with the code change.";
    }

    private static string? Affected(string pointer)
    {
        var segments = pointer.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(s => s.Replace("~1", "/").Replace("~0", "~")).ToArray();
        return segments switch
        {
            ["paths", var path, ..] => "path " + path,
            ["components", "schemas", var schema, ..] => "schema " + schema,
            [var first, ..] => first,
            _ => null,
        };
    }

    private static void Diff(string pointer, JsonNode? expected, JsonNode? actual, List<string> differences)
    {
        if (expected is JsonObject e && actual is JsonObject a)
        {
            foreach (var key in e.Select(p => p.Key).Union(a.Select(p => p.Key)).Order(StringComparer.Ordinal))
            {
                var child = pointer + "/" + key.Replace("~", "~0").Replace("/", "~1");
                var inExpected = e.ContainsKey(key);
                var inActual = a.ContainsKey(key);
                if (inExpected && !inActual)
                {
                    differences.Add("missing from served " + child);
                }
                else if (!inExpected)
                {
                    differences.Add("not in contract " + child);
                }
                else
                {
                    Diff(child, e[key], a[key], differences);
                }
            }

            return;
        }

        if (!JsonNode.DeepEquals(expected, actual))
        {
            differences.Add("changed " + pointer);
        }
    }
}
