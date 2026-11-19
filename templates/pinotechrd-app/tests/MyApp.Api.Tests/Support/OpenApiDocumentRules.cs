using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using MyApp.BuildingBlocks.Web;
using MyApp.BuildingBlocks.Web.Authorization;

namespace MyApp.Api.Tests.Support;

/// <summary>Rules that every OpenAPI document of the API must satisfy, shared by the file and the served documents.</summary>
internal static partial class OpenApiDocumentRules
{
    private static readonly string[] BodyStatuses = ["408", "413", "415"];

    /// <summary>
    /// What the endpoints of a host imply for their documented operations, keyed by <c>METHOD path</c> as the document
    /// writes it. There are three kinds of operation: anonymous (<c>AllowAnonymous()</c>: no security requirement, no 401,
    /// no 403, unless the endpoint declares one of them itself), authenticated (<c>AuthenticatedOnly()</c>: the <c>Bearer</c> requirement and 401, no 403) and permission
    /// (<c>RequirePermission</c>: the requirement, 401 and 403). <see cref="Authenticated"/> holds both of the last two
    /// kinds, <see cref="Permission"/> the last one only. <see cref="Unlimited"/> holds the operations that opted out of
    /// rate limiting (and so do not declare 429). <see cref="Declared"/> holds <c>METHOD path STATUS</c> for each 401 or 403 an
    /// endpoint declares itself (<c>ProducesProblem</c>): an anonymous operation such as the sign-in, whose own answers include
    /// 401 and 403, documents exactly those, and nothing is implied for it.
    /// </summary>
    public sealed record EndpointExpectations(
        IReadOnlySet<string> Authenticated, IReadOnlySet<string> Permission, IReadOnlySet<string> Unlimited, IReadOnlySet<string>? Declared = null)
    {
        /// <summary>Whether the endpoint of <paramref name="key"/> (<c>METHOD path</c>) declares <paramref name="status"/> itself.</summary>
        public bool DeclaresItself(string key, string status) => Declared?.Contains($"{key} {status}") == true;
    }

    /// <summary>Reads the access declaration and the rate limiting metadata of the endpoints registered in the host.</summary>
    public static EndpointExpectations ExpectationsOf(IServiceProvider services)
    {
        var authenticated = new HashSet<string>(StringComparer.Ordinal);
        var permission = new HashSet<string>(StringComparer.Ordinal);
        var unlimited = new HashSet<string>(StringComparer.Ordinal);
        var declared = new HashSet<string>(StringComparer.Ordinal);
        var endpoints = services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>();
        foreach (var endpoint in endpoints)
        {
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [];
            var path = RouteParameter().Replace(endpoint.RoutePattern.RawText ?? string.Empty, "{$1}");
            var requiresPermission = endpoint.Metadata.OfType<PermissionMetadata>().Any();
            var requiresAuthentication = requiresPermission
                || (endpoint.Metadata.OfType<AuthenticatedOnlyMetadata>().Any() && !endpoint.Metadata.OfType<IAllowAnonymous>().Any());
            var disabled = endpoint.Metadata.OfType<DisableRateLimitingAttribute>().Any();
            var ownStatuses = endpoint.Metadata.OfType<IProducesResponseTypeMetadata>()
                .Select(m => m.StatusCode).Where(code => code is 401 or 403).Distinct().ToArray();
            foreach (var method in methods)
            {
                var key = $"{method.ToUpperInvariant()} {path}";
                if (requiresAuthentication)
                {
                    authenticated.Add(key);
                }

                if (requiresPermission)
                {
                    permission.Add(key);
                }

                if (disabled)
                {
                    unlimited.Add(key);
                }

                foreach (var status in ownStatuses)
                {
                    declared.Add($"{key} {status}");
                }
            }
        }

        return new EndpointExpectations(authenticated, permission, unlimited, declared);
    }

    [GeneratedRegex(@"\{([^}:?*]+)[^}]*\}")]
    private static partial Regex RouteParameter();

    /// <summary>
    /// Every operation declares 500 as problem+json, and every operation under the API prefix also 429 (rate limited)
    /// unless the endpoint opted out of rate limiting; 401 and the <c>Bearer</c> security requirement if and only if the
    /// endpoint requires authentication, and 403 if and only if it requires a permission (a 401 or 403 the endpoint declares
    /// itself is also expected, never the security requirement; all read from
    /// <paramref name="expectations"/>); 400 if and only if it has input (parameters or a body), and 408, 413 and 415 if
    /// and only if it has a request body. The document declares the <c>Bearer</c> scheme and no document-wide requirement.
    /// </summary>
    public static void AssertCommonProblemResponses(JsonObject document, EndpointExpectations expectations)
    {
        var operations = OpenApiContract.Operations(document);
        Assert.NotEmpty(operations);

        var failures = new List<string>();
        AssertBearerScheme(document, failures);
        foreach (var (path, method, operation) in operations)
        {
            var name = $"{method.ToUpperInvariant()} {path}";
            var hasBody = operation["requestBody"] is not null;
            var hasInput = hasBody || OpenApiContract.HasParameters(document, path, operation);

            Expect("500", true);
            var key = $"{method.ToUpperInvariant()} {path}";
            Expect("401", expectations.Authenticated.Contains(key) || expectations.DeclaresItself(key, "401"));
            Expect("403", expectations.Permission.Contains(key) || expectations.DeclaresItself(key, "403"));
            ExpectSecurity(expectations.Authenticated.Contains(key));
            if (expectations.Unlimited.Contains(key))
            {
                Expect("429", false);
            }
            else if (path.StartsWith(EndpointRegistration.RoutePrefix, StringComparison.Ordinal))
            {
                Expect("429", true);
            }

            Expect("400", hasInput);
            foreach (var status in BodyStatuses)
            {
                Expect(status, hasBody);
            }

            void ExpectSecurity(bool required)
            {
                var requirements = operation["security"] as JsonArray ?? [];
                var bearer = requirements.Count == 1
                    && requirements[0] is JsonObject requirement
                    && requirement.Count == 1
                    && requirement["Bearer"] is JsonArray { Count: 0 };
                if (required && !bearer)
                {
                    failures.Add($"{name} does not declare exactly the Bearer security requirement");
                }
                else if (!required && requirements.Count > 0)
                {
                    failures.Add($"{name} declares a security requirement but is open to anonymous callers");
                }
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

    private static void AssertBearerScheme(JsonObject document, List<string> failures)
    {
        var scheme = document["components"]?["securitySchemes"]?["Bearer"] as JsonObject;
        if (scheme is null
            || scheme["type"]?.GetValue<string>() != "http"
            || !string.Equals(scheme["scheme"]?.GetValue<string>(), "bearer", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("The document does not declare the Bearer scheme as an http bearer scheme");
        }

        if (document["components"]?["securitySchemes"] is JsonObject schemes && schemes.Count != 1)
        {
            failures.Add("The document declares security schemes other than Bearer");
        }

        if (document["security"] is JsonArray { Count: > 0 })
        {
            failures.Add("The document declares a document-wide security requirement, which would cover anonymous operations");
        }
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
