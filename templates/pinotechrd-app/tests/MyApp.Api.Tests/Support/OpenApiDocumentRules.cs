using System.Text.Json.Nodes;

namespace MyApp.Api.Tests.Support;

/// <summary>Rules that every OpenAPI document of the API must satisfy, shared by the file and the served documents.</summary>
internal static class OpenApiDocumentRules
{
    private static readonly string[] BodyStatuses = ["408", "413", "415"];

    /// <summary>
    /// Every operation declares 500 as problem+json; it declares 400 if and only if it has input (parameters or a
    /// body), and 408, 413 and 415 if and only if it has a request body.
    /// </summary>
    public static void AssertCommonProblemResponses(JsonObject document)
    {
        var operations = OpenApiContract.Operations(document);
        Assert.NotEmpty(operations);

        var failures = new List<string>();
        foreach (var (path, method, operation) in operations)
        {
            var name = $"{method.ToUpperInvariant()} {path}";
            var hasBody = operation["requestBody"] is not null;
            var hasInput = hasBody || OpenApiContract.HasParameters(document, path, operation);

            Expect("500", true);
            Expect("400", hasInput);
            foreach (var status in BodyStatuses)
            {
                Expect(status, hasBody);
            }

            void Expect(string status, bool declared)
            {
                var actual = operation["responses"]?[status] is not null;
                if (declared && OpenApiContract.ResponseSchema(operation, status, OpenApiContract.ProblemJson) is null)
                {
                    failures.Add($"{name} does not declare {status} as {OpenApiContract.ProblemJson}");
                }
                else if (!declared && actual)
                {
                    failures.Add($"{name} declares {status} but has {(hasBody ? "a body" : hasInput ? "parameters only" : "no input")}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>
    /// The schemas that the problem responses point to require title, status, code and traceId; status is an
    /// integer, code and traceId are strings, and code is an open set (no enum). A schema used by a 400 response
    /// also declares <c>errors</c>.
    /// </summary>
    public static void AssertProblemSchemas(JsonObject document)
    {
        var referenced = OpenApiContract.ProblemSchemaNames(document);
        Assert.NotEmpty(referenced);

        foreach (var name in referenced)
        {
            var schema = document["components"]!["schemas"]![name]!.AsObject();
            var required = schema["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToArray() ?? [];
            foreach (var property in new[] { "title", "status", "code", "traceId" })
            {
                Assert.True(required.Contains(property), $"Schema {name} does not list '{property}' as required.");
                Assert.True(schema["properties"]?[property] is not null, $"Schema {name} does not declare '{property}'.");
            }

            Assert.True(IsType(schema["properties"]!["status"]!.AsObject(), "integer"), $"'status' of schema {name} is not a plain integer.");
            Assert.True(IsType(schema["properties"]!["code"]!.AsObject(), "string"), $"'code' of schema {name} is not a string.");
            Assert.True(IsType(schema["properties"]!["traceId"]!.AsObject(), "string"), $"'traceId' of schema {name} is not a string.");
            Assert.True(schema["properties"]!["code"]!["enum"] is null, $"'code' of schema {name} has an enum; codes are only ever added.");
        }

        var validation = OpenApiContract.Operations(document)
            .Select(o => OpenApiContract.ResponseSchema(o.Operation, "400", OpenApiContract.ProblemJson))
            .OfType<JsonObject>()
            .Select(s => OpenApiContract.ResolveSchema(document, s))
            .ToArray();
        Assert.All(validation, s => Assert.NotNull(s["properties"]?["errors"]));
    }

    /// <summary>Every operation has at least one tag and none is the name of the API assembly (the default tag).</summary>
    public static void AssertTags(JsonObject document)
    {
        var operations = OpenApiContract.Operations(document);
        Assert.NotEmpty(operations);

        var untagged = operations.Where(o => OpenApiContract.Tags(o.Operation).Length == 0).Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}").ToArray();
        Assert.True(untagged.Length == 0, "Operations without a tag: " + string.Join(", ", untagged));

        var defaulted = operations
            .Where(o => OpenApiContract.Tags(o.Operation).Contains(OpenApiContract.ApiProjectName, StringComparer.Ordinal))
            .Select(o => $"{o.Method.ToUpperInvariant()} {o.Path}")
            .ToArray();
        Assert.True(defaulted.Length == 0, $"Operations tagged with the assembly name '{OpenApiContract.ApiProjectName}': " + string.Join(", ", defaulted));
    }

    private static bool IsType(JsonObject property, string type) => property["type"] is JsonValue value && value.GetValue<string>() == type;
}
