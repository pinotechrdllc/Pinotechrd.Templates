using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MyApp.BuildingBlocks.Web.Problems;

/// <summary>
/// Names the keys of <c>errors</c> like the members of the JSON the client sent. The built-in validation reports
/// C# property paths (<c>HomeAddress.Street</c>, <c>Lines[1].Quantity</c>) and an <c>Error</c> reports whatever keys
/// it was built with; both are rewritten segment by segment with the API's JSON naming policy (camelCase by
/// default), keeping dots and indexes: <c>homeAddress.street</c>, <c>lines[1].quantity</c>.
/// </summary>
internal static class ValidationErrorKeys
{
    /// <summary>Rewrites the keys of <paramref name="problem"/>; messages of keys that become equal are merged.</summary>
    public static void Normalize(HttpValidationProblemDetails problem, HttpContext httpContext)
    {
        var policy = httpContext.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions.PropertyNamingPolicy;
        if (policy is null || problem.Errors.Count == 0)
        {
            return;
        }

        var original = problem.Errors.ToArray();
        problem.Errors.Clear();
        foreach (var (key, messages) in original)
        {
            var name = ConvertPath(key, policy);
            problem.Errors[name] = problem.Errors.TryGetValue(name, out var existing) ? [.. existing, .. messages] : messages;
        }
    }

    private static string ConvertPath(string path, JsonNamingPolicy policy) =>
        string.Join('.', path.Split('.').Select(segment => ConvertSegment(segment, policy)));

    private static string ConvertSegment(string segment, JsonNamingPolicy policy)
    {
        var index = segment.IndexOf('[', StringComparison.Ordinal);
        var name = index < 0 ? segment : segment[..index];
        var converted = name.Length == 0 ? name : policy.ConvertName(name);
        return index < 0 ? converted : converted + segment[index..];
    }
}
