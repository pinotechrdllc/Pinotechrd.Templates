using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Validation;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// Which assemblies called <c>AddValidation()</c>. The built-in validation is generated per project, and each call adds
/// the generated resolver of the project that made it to <see cref="ValidationOptions.Resolvers"/>, so the assembly of a
/// resolver is the observable fact that its project made the call.
/// </summary>
internal static class ValidationResolvers
{
    /// <summary>The names of the assemblies that hold a validation resolver registered in <paramref name="services"/>.</summary>
    public static IReadOnlyList<string> AssemblyNamesOf(IServiceProvider services)
    {
        // The validation API is experimental (ASP0029); this is the only place that uses it directly.
#pragma warning disable ASP0029
        var resolvers = services.GetRequiredService<IOptions<ValidationOptions>>().Value.Resolvers;
        return [.. resolvers.Select(r => r.GetType().Assembly.GetName().Name!).Distinct(StringComparer.Ordinal)];
#pragma warning restore ASP0029
    }
}
