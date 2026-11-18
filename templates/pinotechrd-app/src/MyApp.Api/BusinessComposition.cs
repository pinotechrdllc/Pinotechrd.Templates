namespace MyApp.Api;

/// <summary>
/// Joins the business code to the host. Shared by both architectures: the parts that differ are partial methods
/// declared here and implemented, when the architecture has something to add, in a file of their own that only that
/// architecture keeps (<c>BusinessComposition.Modular.cs</c> adds the modules). A partial method with no
/// implementation is not dead code: the compiler removes the call, so this file needs no conditional text.
/// </summary>
public static partial class BusinessComposition
{
    /// <summary>Registers the business code of the application: in a modular application, each of its modules once.</summary>
    /// <param name="builder">The builder of the host.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHostApplicationBuilder AddBusiness(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        AddModules(builder);
        return builder;
    }

    /// <summary>
    /// Hook for the modules: implemented in <c>BusinessComposition.Modular.cs</c>, which lists each module once. Without
    /// that file (a clean application) the call to it is removed at compile time.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    static partial void AddModules(IHostApplicationBuilder builder);
}
