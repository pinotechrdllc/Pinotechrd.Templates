using MyApp.BuildingBlocks.Web;
using MyApp.Modules.Access.Endpoints;

namespace MyApp.Api;

/// <summary>
/// Joins the business code to the host. Shared by both architectures: the Access module (users, roles, permissions and
/// the security library) is composed here in both, and the parts that differ are partial methods declared here and
/// implemented, when the architecture has something to add, in a file of their own that only that architecture keeps
/// (<c>BusinessComposition.Clean.cs</c> registers the layers of a clean application, <c>BusinessComposition.Modular.cs</c>
/// adds the modules). A partial method with no implementation is not dead code: the compiler removes the call, so this
/// file needs no conditional text.
/// </summary>
public static partial class BusinessComposition
{
    /// <summary>
    /// Registers the business code of the application: the Access module, then, in a clean application, the services of
    /// its layers (the <c>*Handler</c> classes of the application layer); in a modular application, each of its modules
    /// once.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IHostApplicationBuilder AddBusiness(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddModule<AccessModule>();
        AddLayers(builder);
        AddModules(builder);
        return builder;
    }

    /// <summary>
    /// Hook for the layers of a clean application: implemented in <c>BusinessComposition.Clean.cs</c>, which registers
    /// the handlers of the application layer and, later, the services of the infrastructure layer. Without that file
    /// (a modular application) the call to it is removed at compile time.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    static partial void AddLayers(IHostApplicationBuilder builder);

    /// <summary>
    /// Hook for the modules: implemented in <c>BusinessComposition.Modular.cs</c>, which lists each module once. Without
    /// that file (a clean application) the call to it is removed at compile time.
    /// </summary>
    /// <param name="builder">The builder of the host.</param>
    static partial void AddModules(IHostApplicationBuilder builder);
}
