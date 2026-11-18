using Microsoft.Extensions.DependencyInjection;

namespace MyApp.Api.Tests.Support;

/// <summary>
/// The one call to <c>AddValidation()</c> of the test assembly. The built-in validation generator emits its code per
/// project and fails the build with a second call site in the same project, so the host factory and the fixture module
/// that stands for a module calling it both go through here.
/// </summary>
internal static class TestValidation
{
    /// <summary>Adds the validation generated for the endpoints of the test assembly.</summary>
    public static IServiceCollection AddTestAssemblyValidation(this IServiceCollection services) => services.AddValidation();
}
